# iRoute

iRoute is an open-source, task-aware AI execution runtime. It resolves work from trusted state first, sends only unresolved work to capabilities or models, validates the result, and materializes reusable project artifacts with evidence and cost metadata.

## Current milestone

Formal backlog status: **M0 and M1 are complete; W08 is complete**. See [the workstream status](docs/workstream-status.md). M2 continues with W09.

The first end-to-end P0 slice is operational for `email.draft`:

- synchronous task execution through ASP.NET Core
- tenant-scoped idempotency and artifact reuse
- bounded context compilation with a context manifest
- deterministic development gateway or configurable HTTP model gateway
- fail-closed output and quality validation
- durable SQLite and PostgreSQL stores for executions, ordered events, versioned artifacts, facts, decisions, and dependency edges
- cancellation requests, deadlines, health checks, and SSE event replay
- bounded dependency scheduling with durable per-step checkpoints and restart-safe resume
- optional JWT authentication with claim-derived tenant and actor identity
- capability allow lists and authenticated permission-scope enforcement
- durable external-action approvals, restart-safe resumption, and idempotent action results
- deterministic artifact/memory supersession with tenant-scoped, dependency-aware invalidation
- explainable no-model resolution from exact results, project facts/decisions, explicit artifacts, and registered deterministic handlers
- ranked context compilation with explicit artifact sections, full-history exclusion, serialized token bounds, and fact-level provenance
- measured direct routing, bounded workflow planning, model-profile selection, and explainable quality-driven escalation
- versioned schema migration shared by SQLite and PostgreSQL
- working .NET and Node.js clients

This is a development milestone, not a production release. Durable worker leasing, distributed action reconciliation, and real connector execution are still ahead. The current `email.send` executor is deterministic and development-only.

## Quick start

Prerequisite: .NET SDK `10.0.100` or newer on the .NET 10 line. The repository is currently verified with SDK `10.0.102`.

```bash
dotnet restore iRoute.slnx
dotnet build iRoute.slnx --no-restore
dotnet run --project src/iRoute.Api -- --urls http://localhost:8080
```

The default developer profile creates `iroute.db` and uses the deterministic gateway, so it needs no provider credential. In another terminal:

```bash
curl --request POST http://localhost:8080/v1/executions \
  --header 'Content-Type: application/json' \
  --header 'X-Tenant-Id: demo' \
  --header 'X-Actor-Id: founder' \
  --header 'Idempotency-Key: email-draft-001' \
  --data @examples/email-draft.json
```

The first request returns a validated `SmallModel` outcome and an `email.draft` artifact. Send the same input with a new idempotency key and the runtime returns `ExactArtifact` with zero model calls.

Useful endpoints:

- `GET /v1/executions/{executionId}`
- `GET /v1/executions/{executionId}/events?after=0`
- `POST /v1/executions/{executionId}/cancel`
- `POST /v1/executions/{executionId}/approvals`
- `GET /v1/artifacts/{artifactId}`
- `GET /health/live`, `GET /health/ready`, and `GET /openapi/v1.json`

## Verification

```bash
dotnet run --project tests/iRoute.UnitTests --no-build -- -reporter quiet
dotnet run --project tests/iRoute.ArchitectureTests --no-build -- -reporter quiet
npm run test:contracts
npm --prefix sdks/node run check
```

With the API running, execute the initial behavioral evaluation fixture:

```bash
node tools/run-evaluation.mjs
```

To exercise `ModelGateway__Mode=Http` without a provider dependency, start `node tools/gateway-conformance-server.mjs`, point the API at `http://127.0.0.1:5092`, and run the same evaluation. The gateway request/result schemas are under `spec/schemas`.

## Configuration

Use environment variables or standard ASP.NET Core configuration.

| Setting | Purpose | Default |
|---|---|---|
| `Storage__Provider` | `Memory`, `Sqlite`, or `Postgres` | `Sqlite` |
| `Storage__AutoInitialize` | Create the prototype schema at startup | `true` |
| `ConnectionStrings__iRoute` | Durable database connection | `Data Source=iroute.db` |
| `ModelGateway__Mode` | `Deterministic` or `Http` | `Deterministic` |
| `ModelGateway__BaseUrl` | Generic HTTP gateway base URL | unset |
| `ModelGateway__ApiKey` | Generic HTTP gateway bearer credential | unset |
| `Workflow__QueueCapacity` | Maximum queued ready steps per scheduling round | `16` |
| `Workflow__MaxParallelSteps` | Runtime ceiling on parallel steps per execution | `4` |
| `Identity__Mode` | `DevelopmentHeaders` or `Jwt` | `DevelopmentHeaders` |
| `Identity__Authority` | OpenID Connect/JWT issuer | required in JWT mode |
| `Identity__Audience` | Expected JWT audience | required in JWT mode |
| `Identity__TenantClaim` | Claim containing tenant identity | `tenant_id` |
| `Identity__ActorClaim` | Claim containing actor identity | `sub` |
| `Identity__PermissionClaim` | Claim containing space- or comma-separated permission scopes | `scope` |
| `OTEL_EXPORTER_OTLP_ENDPOINT` | Opt-in telemetry export | unset |

Use the deterministic gateway only for local development and repeatable tests. The HTTP mode expects `POST {BaseUrl}/v1/execute` using the provider-neutral gateway contract.

`DevelopmentHeaders` trusts `X-Tenant-Id`, `X-Actor-Id`, and `X-Permission-Scopes` and must not be exposed as an internet-facing production configuration. JWT mode requires an authenticated token with the configured tenant claim and obtains permission scopes only from `Identity__PermissionClaim`; request headers and body fields cannot elevate them.

## Architecture and source of truth

The dependency rule is `Contracts <- Core <- Runtime <- Infrastructure <- Hosts`; SDKs depend only on the public protocol. See [the architecture guide](docs/architecture.md), [operations guide](docs/operations.md), [contract versioning rules](docs/contract-versioning.md), [SSE event contract](spec/events/sse-v1.md), [error taxonomy](spec/errors/error-taxonomy.v1.md), and [canonical product/engineering specification](docs/iRoute-Product-Engineering-Specification.md).

Public language-neutral contracts live in [OpenAPI](spec/openapi/iroute.v1.yaml) and [JSON Schema](spec/schemas). The [documentation map](docs/README.md) links the canonical Markdown sources. iRoute Core, contracts, official SDKs, and self-hosting remain Apache 2.0.
