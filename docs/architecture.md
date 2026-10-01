# Architecture

iRoute is a modular .NET monolith with one executable and five production
projects. Project boundaries express ownership; feature folders keep related
code together inside each project. There are no `Application`, `Infrastructure`,
`Hosts`, or `Clients` wrapper trees.

## Dependency graph

```text
                         ┌──────────────────────┐
                         │    iRoute.Runtime    │
                         │ API · worker · CLI   │
                         │ migrations · DI      │
                         └───┬────┬────┬───────┘
                             │    │    │
                    ┌────────┘    │    └────────┐
                    v             v             v
             iRoute.Core   iRoute.Services   iRoute.Data
                    │             │             │
                    └─────────────┼─────────────┘
                                  v
                            iRoute.Common
```

Allowed references:

- Common references no iRoute project.
- Services, Data, and Core each reference only Common.
- Runtime references all four libraries and is the only composition root.
- Tests may reference every production project.

`ProjectDependencyTests` verifies both the reference graph and that exported
interfaces, records, and enums do not leak into implementation assemblies.

## Project ownership

### Common

Common is the single contract boundary. It owns public request/response DTOs,
execution snapshots and events, routing and gateway envelopes, capability
contracts, persistence ports, policy interfaces, shared options, enums, and
primitives. It contains no routing, persistence, provider, or hosting behavior.

Adding an interface to Services or Data because an implementation needs it is
not allowed when another project must consume it; the port belongs in Common.

### Services

Services owns behavior: request validation, deterministic resolution, context
compilation, routing, planning, execution scheduling, approval policy,
capability invocation, gateway execution and resilience, result validation,
materialization, observability projection, and task/model registries.

Services depends on Common ports rather than Entity Framework or ASP.NET Core.
Focused provider adapters implement `IModelGateway`: OpenAI Responses, Anthropic
Messages, generic HTTP/NDJSON, and local subscription access. Their protocols
stay inside Services and do not leak into Core or endpoint behavior.

### Focused execution ownership

`ExecutionService` is a small dispatcher, not a partial-class container. Its five
dependencies own submission, approval, queued work, cancellation, and action
reconciliation. Submission delegates resolution and preparation; plan execution
delegates model/capability/action steps and outcome materialization. Persistence
and gateway audit recording are explicit shared collaborators. Each class has
one ownership reason, and constructor-dependency tests guard against rebuilding
the former 22-dependency execution class.

HTTP cancellation and reconciliation endpoints resolve caller identity, call
Core, and translate results/errors. Services owns tenant visibility, permission
and state decisions, durable changes, cancellation signals, and audit events.
The same commands are available to an embedded .NET caller through Common.

### Data

Data owns persistence implementations: `IRouteDbContext`, entities,
SQLite/PostgreSQL configuration, migrations, durable queues and leases,
artifact/memory stores, circuit state, observability stores, and in-memory
development alternatives. Data does not decide routing or policy.

### Core

Core is intentionally small. `ExecutionOrchestrator` is the stable facade used
by Runtime to submit work through Common ports. Business rules do not accumulate
here; Services owns them.

### Runtime

Runtime is the outer boundary and the only executable. It owns:

- ASP.NET Core endpoint mapping, identity, OpenAPI, health, static dashboard,
  and HTTP serialization;
- dependency injection and startup validation;
- execution/lifecycle background hosts;
- explicit schema migration commands;
- the built-in typed HTTP client and CLI surface;
- OpenTelemetry host configuration.

The executable selects a mode rather than creating another host project:

```text
iroute serve      API and optional embedded workers
iroute worker     background workers only
iroute migrate    schema administration
iroute client     HTTP client commands
```

## Execution flow

1. Runtime authenticates the caller and derives tenant, actor, and permission
   scopes from JWT claims or development headers. Development headers are
   unauthenticated: the operator must bind the server/forwarded container port
   to loopback and must not expose this identity mode publicly.
2. Runtime maps the `v1` request to Common contracts and calls Core.
3. Core delegates submission to Services.
4. Services validates the request and checks idempotency, exact results,
   artifacts, facts, decisions, and deterministic handlers.
5. If work remains, Services compiles bounded context, selects a route, builds a
   bounded plan, and persists the queued execution through Common ports.
6. An execution worker leases the execution. Services invokes registered
   capabilities or the provider-neutral model gateway, honoring deadline,
   permission, approval, retry, and circuit policy.
7. Services validates output, records evidence and normalized usage (including
   whether cost is known), and asks
   Data-backed ports to persist events, checkpoints, artifacts, and memory.
8. Runtime exposes snapshots, replayable events, artifacts, and redacted
   observability views to the caller.

No raw provider response, secret, unbounded history, or database entity crosses
the public contract boundary.

Approval decisions require a different actor from the requester, plus the task's
required scopes and `approval:grant`. Revalidation rejects previously recorded
self-approved actions. Cancellation while awaiting approval claims the waiting
state atomically so it cannot overwrite an execution already resumed by another
caller. Distributed cancellation during a running external action remains
best-effort and does not undo a provider side effect.

See [model providers](model-providers.md) for API versus subscription access,
unknown-cost semantics, native local ChatGPT sign-in, and credential-storage limits.

## Runtime and storage shapes

SQLite runs one `serve` process with embedded execution and lifecycle workers.
It is the zero-dependency development shape.

PostgreSQL separates process modes while using the same binary and image:

- API replicas run `serve` with embedded workers disabled.
- execution workers run `worker` and use fenced database leases.
- exactly one lifecycle worker performs cleanup for a database.
- a short-lived migration process runs `migrate up` before rollout.

Execution workers may scale horizontally. Lease ownership, heartbeats,
checkpoint state, cancellation, and circuit probes are durable. Lifecycle work
is singleton by deployment convention until a database-backed lifecycle lease is
introduced.

## Contracts and serialization

The wire source of truth is `spec/`; Common is its .NET representation.
System.Text.Json source generation is used at known HTTP and gateway boundaries.
Public additions must update code, OpenAPI/schema material, compatibility
snapshot, tests, and documentation together.

Events are append-only and ordered. Observability projects those events into
bounded, tenant-scoped views rather than creating a second behavioral source of
truth.

## .NET design rules

- Use `TimeProvider` for time and delay behavior.
- Bind options by feature, validate them at startup, and keep cross-property
  rules in focused `IValidateOptions<T>` implementations.
- Prefer records for immutable contracts, frozen collections for built-in
  registries, `BackgroundService` for hosted loops, `HttpClientFactory` for
  transport lifetime, generated logging, health checks, and OpenTelemetry.
- Keep policy-sensitive attempts, costs, deadlines, and retries explicit. Do
  not hide them in generic HTTP retry middleware.
- Split large files by cohesive responsibility and feature, not arbitrary
  technical layer names or partial-class fragments.
- Keep dependency injection at Runtime composition. Implementation projects do
  not become service locators.

## Change test

A proposed type should answer one question:

- Is it a shared shape or port? Common.
- Is it task/routing/execution behavior? Services.
- Is it persistence or migration behavior? Data.
- Is it the stable submission facade? Core.
- Is it hosting, transport, CLI, or composition? Runtime.
- Is it verification? Tests.

If the answer spans projects, split the contract from the implementation rather
than adding a reverse reference.
