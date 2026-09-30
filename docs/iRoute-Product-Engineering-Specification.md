# iRoute product and engineering specification

Status: canonical development specification for `0.1.0-alpha.4`.

## 1. Product definition

iRoute is a task-aware AI execution runtime. It accepts a typed task, resolves
what it can from trusted project state, invokes only the capabilities or model
gateway needed for unresolved work, validates the outcome, and materializes
reusable state with provenance and normalized usage evidence.

The optimization target is successful task completion under explicit quality,
cost, latency, permission, and safety constraints—not maximum model calls or a
provider-specific chat abstraction.

## 2. Product boundary

iRoute owns:

- task definitions, validation, policy, permissions, and approvals;
- exact-state and deterministic resolution;
- bounded evidence/context compilation;
- routing, model-profile selection, escalation, and bounded planning;
- capability/model-gateway orchestration and normalized failure behavior;
- durable execution state, events, checkpoints, artifacts, memory, and evidence;
- tenant-scoped observability and lifecycle policy.

iRoute does not own:

- model hosting or provider-specific SDKs/protocols;
- organization identity issuance, billing reconciliation, or secret storage;
- arbitrary autonomous browser/computer control;
- unbounded conversation/history storage;
- production connector credentials or downstream-system authorization policy.

Provider and connector adaptation belongs behind registered, bounded contracts.

## 3. Users

- A developer runs the deterministic SQLite profile and submits tasks without a
  provider key.
- A platform team deploys the same runtime with PostgreSQL, JWT, workers,
  migrations, generic gateways, telemetry, and managed secrets.
- An operator observes tenant-scoped executions, quality, latency, reported
  cost, reuse, gateway health, and circuit behavior.
- An approver explicitly accepts or rejects external writes allowed by task
  policy and permission scope.

## 4. Product principles

1. Resolve before generating.
2. Treat project state as typed, versioned, scoped, expiring evidence.
3. Make routing, attempts, fallbacks, cost, and quality explainable.
4. Bound context, output, parallelism, retries, deadlines, and retention.
5. Fail closed on identity, permission, validation, and external writes.
6. Make durable work observable and resumable rather than hiding it in one HTTP request.
7. Keep provider protocols and credentials outside the execution core.
8. Measure before claiming quality, cost, latency, or production readiness.

## 5. Supported task flow

For each accepted task:

1. Derive tenant, actor, and permissions from the authenticated request.
2. Validate type, input, constraints, idempotency, and policy.
3. Check exact previous outcomes, artifacts, facts, decisions, and deterministic handlers.
4. Compile a bounded context manifest from eligible, fresh, dependency-valid evidence.
5. Select a direct route or bounded plan and record the reasons.
6. Persist the execution and ordered event before asynchronous processing.
7. Lease work, invoke capabilities/model gateways, and persist checkpoints.
8. Validate structure, policy, evidence, and quality.
9. Materialize artifacts/memory with lineage, dependencies, expiry, and provenance.
10. Publish a terminal snapshot and event with normalized usage/failure evidence.

Every stage observes the task deadline and caller cancellation where safe.
External side effects use approval and idempotency boundaries; transport timeout
alone cannot prove whether a remote side effect occurred.

## 6. Repository architecture

The implementation is .NET-only and contains exactly six projects:

| Project | Owns |
|---|---|
| Common | every cross-project contract, DTO, interface, port, option, enum, and primitive |
| Services | task/routing/execution/policy/gateway/capability behavior |
| Data | EF Core, entities, SQLite/PostgreSQL stores, migrations, leases, and durable projections |
| Core | small stable execution facade |
| Runtime | API, identity, workers, CLI/client, migrations, telemetry, and DI composition |
| Tests | architecture and behavior verification |

Services, Data, and Core depend only on Common. Runtime depends on all production
libraries. Tests may depend on all. Reverse references and cycles are forbidden.

Contracts exist in Common only. Implementation projects do not export alternate
interfaces, records, or enums. Feature folders organize cohesive behavior inside
the owning project; wrapper layer directories are not used.

## 7. Runtime modes

One executable supports:

- `serve`: HTTP/SSE/OpenAPI/dashboard/health plus optional embedded workers;
- `worker`: durable execution and lifecycle background services;
- `migrate`: explicit schema status, upgrade, and confirmed rollback;
- `client` and shorthand client commands: protocol-only access to a server.

One OCI image contains that executable and defaults to `serve`. Process commands
select roles in Compose and Kubernetes.

## 8. Public contract

The API major is `v1`. The language-neutral contract is:

- `spec/openapi/iroute.v1.yaml`;
- JSON Schemas under `spec/schemas`;
- `spec/events/sse-v1.md`;
- `spec/errors/error-taxonomy.v1.md`;
- the checked compatibility snapshot.

Common is the canonical .NET representation. Additive `v1` fields remain
optional, event consumers ignore unknown types/fields, and clients preserve
unknown error codes. Breaking changes require a new major contract.

Required operation families are execution submission/read/event/cancel,
approval, external-action inspection/reconciliation, artifact read,
observability summary/timeline, gateway health, readiness/liveness, OpenAPI, and
dashboard static content.

## 9. Identity and authorization

Production authenticates JWTs and derives tenant and actor from configured
claims. Request JSON cannot override authenticated identity. Permission scopes
are evaluated at policy, connector, approval, artifact, and reconciliation
boundaries.

Development headers are permitted only in the Development environment and must
not listen beyond loopback. Logs, events, traces, and metrics must not contain
keys, bearer tokens, raw prompts, raw provider responses, or unredacted sensitive
payloads by default.

## 10. Resolution, routing, and planning

Resolution considers only tenant/project-scoped, permission-eligible, fresh,
dependency-valid state. Reuse records the exact source and reason. Superseded,
expired, invalidated, or ambiguous evidence is rejected.

Routing inputs include task definition, constraints, registered capabilities,
model-profile provenance, and measured evidence when available. Built-in
synthetic profiles must be labeled synthetic; a measured profile is eligible as
measured only with provider, model, timestamp, and positive sample count.

Plans are deterministic for identical normalized inputs and bounded by task and
runtime ceilings. Explanations record considered routes, selected route/profile,
quality/cost/latency expectations, escalation reason, and policy version.

## 11. Gateway and capabilities

Model execution crosses one provider-neutral HTTP boundary in buffered or
bounded NDJSON streaming mode. The runtime sends capability/profile identity,
bounded context, deadline, output limit, correlation, and policy ceilings. It
receives projected output, evidence, normalized usage, latency, finish reason,
and classified failure—not a provider SDK object.

Multiple operator-registered routes may support a request. Candidate order and
fallback are deterministic. Gateway resilience owns cross-deployment attempts,
Retry-After handling, persistent circuits, and fenced half-open probes. Workflow
retry does not duplicate model attempts.

Capabilities use one normalized invocation/result/failure contract. Inputs,
outputs, deadlines, permissions, and projected metadata are bounded. External
writes require task permission, caller scope, approval where policy requires,
and durable idempotency/reconciliation state.

## 12. Persistence and lifecycle

SQLite is the local/single-process store. PostgreSQL is the multi-process store.
The durable model includes executions, ordered events, step checkpoints, leases,
artifacts, facts, decisions, dependency edges, external actions, gateway circuit
state, migrations, and observability projection data.

Database entities never become public DTOs. Mutations that coordinate work use
transactions and fencing. Migrations are explicit and additive by default.

Artifacts and memory have tenant/project scope, lineage, dependencies,
provenance, creation/update time, expiry, and invalidation state. Lifecycle work
is bounded by TTL, inactive/archive/delete windows, lineage/tenant limits,
archive retention, and batch size. Dependency invalidation propagates before
unsafe reuse.

## 13. Observability

Every execution has a correlation/trace identity and append-only event history.
Telemetry includes completion/failure, stage latency, normalized tokens and
reported cost, quality, reuse, route/attempt/circuit state, queue/lease behavior,
and lifecycle work with bounded cardinality.

The observability API and dashboard are tenant-scoped and bounded by time,
execution count, event count, string size, and recent-row limits. Metadata-only
payloads are default. Observability is a projection of durable state, not a
second execution source of truth.

## 14. Packaging and delivery

Five NuGet artifacts share one product version: Common, Services, Data, Core,
and the `iRoute` tool. One GHCR image supports Linux AMD64/ARM64 and all runtime
modes. Source archive, package files, release notes, and checksums are attached
to an immutable GitHub prerelease.

CI verifies formatting, compiler/analyzers, tests, packages/tool installation,
Compose, container commands, SQLite/PostgreSQL migrations, separate worker
execution, SSE, idempotency, tenant scoping, API health, OpenAPI, and dashboard.
CodeQL uses an explicit
analyzed build and Gitleaks scans full history. Release publication requires a
GitHub-verified annotated version tag; NuGet uses OIDC trusted publishing, and
container output includes provenance and SBOM.

## 15. Engineering quality

- Nullable analysis, recommended analyzers, code style, and warnings-as-errors
  apply centrally.
- Time-dependent code uses `TimeProvider`.
- Configuration uses typed options and startup validation.
- Known JSON boundaries use System.Text.Json source generation where practical.
- Built-in immutable registries use frozen/read-only collections.
- HTTP lifetime uses `HttpClientFactory`; hidden generic retries are forbidden
  at policy-sensitive gateway boundaries.
- Classes and files are split by cohesive responsibility, with no arbitrary
  partial-class decomposition used to disguise coupling.
- Architecture tests prevent reference drift and duplicate contract assemblies.

## 16. Release and readiness criteria

A release candidate must have synchronized version metadata, current release
notes, a clean tree, passing local and GitHub gates, five verified packages, a
smoke-tested image, migration/rollback guidance, and no known secret exposure.

Private beta additionally requires tenant quota/fair scheduling, deeper
identity/persistence/routing/migration coverage, public-contract drift tests,
production connector review, load/soak/chaos evidence, backup/restore evidence,
and closure or explicit acceptance of security findings.

`1.0` requires a published support window, stable contract/migration policy,
operational SLOs, incident-response process, real measured routing evidence, and
multiple production-like deployments demonstrating safe upgrade, rollback, and
failure recovery.

## 17. Open-source boundary

iRoute core runtime, contracts, self-hosting, the .NET tool/packages, public
specification, migration system, and required operational telemetry are Apache
2.0. Commercial value may be built around managed hosting, enterprise
operations/governance, support, SLAs, and services without weakening the open
self-hosted product.
