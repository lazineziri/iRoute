# iRoute

iRoute is an open-source, task-aware AI execution runtime. It resolves work from trusted state first, sends only unresolved work to capabilities or models, validates the result, and materializes reusable project artifacts with evidence and cost metadata.

> **Experimental alpha:** `0.1.0-alpha.2` is intended for evaluation and controlled
> pilots. Breaking changes are expected before `1.0`. There is no production or
> security-response SLA. Reference connectors are not production integrations,
> and checked provider cost/performance figures are not production measurements.

## Current release

The first adoption-ready source baseline is `0.1.0-alpha.2`. See the
[release notes](docs/releases/0.1.0-alpha.2.md) for its verified capabilities,
known limits, and upgrade guidance.

The first end-to-end P0 slice is operational for `email.draft`:

- `202 Accepted` submission through ASP.NET Core with durable asynchronous execution workers
- tenant-scoped idempotency and artifact reuse
- bounded context compilation with a context manifest
- deterministic development gateway or configurable buffered/streaming HTTP model gateway
- fail-closed output and quality validation
- durable SQLite and PostgreSQL stores for executions, ordered events, versioned artifacts, facts, decisions, and dependency edges
- cancellation requests, deadlines, health checks, and SSE event replay
- bounded dependency scheduling with durable per-step checkpoints and restart-safe resume
- PostgreSQL-backed work claims, fenced leases, heartbeats, crash takeover, distributed cancellation, and approval requeueing
- bounded classified retries with exponential backoff, deterministic jitter, timeouts, and `Retry-After` support
- optional JWT authentication with claim-derived tenant and actor identity
- capability allow lists and authenticated permission-scope enforcement
- durable external-action approvals, restart-safe resumption, and idempotent action results
- deterministic artifact/memory supersession with tenant-scoped, dependency-aware invalidation
- explainable no-model resolution from exact results, project facts/decisions, explicit artifacts, and registered deterministic handlers
- ranked context compilation with explicit artifact sections, full-history exclusion, serialized token bounds, and fact-level provenance
- provenance-bearing direct routing, bounded workflow planning, model-profile selection, and explainable quality-driven escalation
- provider-neutral gateway deadlines, cancellation, normalized usage/latency, health, and classified failure reporting
- multiple registered generic gateway deployments with deterministic policy fallback and durable per-deployment circuit breaking
- fenced half-open probes, Retry-After-aware open intervals, exhaustion evidence, and gateway/deployment resilience metrics
- versioned golden evaluation across every built-in task, task-specific quality/safety scoring, cost/latency benchmarks, and a source-bound routing regression gate
- one normalized capability contract with reference email, calendar, read-only database, registered OpenAPI, registered MCP, and typed agent-result connectors
- connector projection before model context, bounded outputs/deadlines, classified failures, and write reuse through the approval/idempotency boundary
- configurable artifact/memory TTLs, lineage and tenant quotas, dependency-safe cold archival, and bounded archive retention
- asynchronous lifecycle sweeps with recursive invalidation, deletion propagation, supersession-pointer repair, and dangling-index checks
- end-to-end OpenTelemetry execution spans and cost, token, quality, latency, reuse, and completion metrics
- a tenant-scoped observability read model with redacted timelines, task/policy comparisons, and memory-hit diagnostics
- a dependency-free operator dashboard at `/dashboard/` with bounded query windows and metadata-only timelines by default
- versioned schema migration shared by SQLite and PostgreSQL
- working .NET, Node.js, Python, Java, PHP, and Rust clients with shared conformance fixtures, published on NuGet, npm, PyPI, Packagist, and crates.io; the Java client installs from source until Maven Central publication lands
- a thin `iroute` CLI and runnable quick starts for every official SDK
- non-root SQLite/PostgreSQL container profiles, explicit schema migrations, and horizontally scalable Kubernetes API manifests
- Apache-2.0 community governance, private security reporting, explicit compatibility windows, and reproducible checksummed release artifacts

This is a development milestone, not a production release. Durable execution leasing and deterministic provider fallback/circuit breaking are implemented, but distributed action reconciliation, tenant quotas, sustained load/soak testing, production failover validation, and production transport adapters are still ahead. Run one lifecycle worker per database; execution workers may scale horizontally. The W11 connectors are deterministic reference adapters; `email.send` remains simulated and approval-gated.

## Quick start

For a clean clone or source archive, follow the [installation guide](docs/installation.md).

Prerequisite: .NET SDK `10.0.100` or newer on the .NET 10 line. The repository is currently verified with SDK `10.0.102`.

iRoute has one runtime and two distribution surfaces. The API, execution worker,
and migration job run as containers. Applications install one thin language SDK
to call that runtime. Installing an SDK does not embed or start iRoute, and
self-hosters may call the HTTP API directly without an SDK.

```bash
dotnet restore iRoute.slnx
dotnet build iRoute.slnx --no-restore
ASPNETCORE_ENVIRONMENT=Development dotnet run --project src/iRoute.Api -- --urls http://localhost:8080
```

In a second terminal, start the execution and lifecycle host:

```bash
dotnet run --project src/iRoute.Worker
```

The default developer profile uses the deterministic gateway, so it needs no provider credential. A relative SQLite `Data Source` is resolved against one per-user directory (`%LOCALAPPDATA%\iRoute` on Windows, `~/Library/Application Support/iRoute` on macOS, `$XDG_DATA_HOME/iRoute` on Linux) so the API and worker share one database regardless of the directory each host is started from. Set `ConnectionStrings__iRoute` to an absolute path to choose your own location. In another terminal:

```bash
curl --request POST http://localhost:8080/v1/executions \
  --header 'Content-Type: application/json' \
  --header 'X-Tenant-Id: demo' \
  --header 'X-Actor-Id: founder' \
  --header 'Idempotency-Key: email-draft-001' \
  --data @examples/email-draft.json
```

The first request returns HTTP `202` with a durable execution ID, normally in `Queued`; poll `GET /v1/executions/{executionId}` or reconnect to its SSE stream until it is terminal. Send the same input with a new idempotency key and the no-model fast path can return `ExactArtifact` immediately with zero model calls and no worker-queue entry.

The CLI uses the same local defaults and delegates to the .NET SDK:

```bash
dotnet run --project src/iRoute.Cli -- \
  execute --request @examples/email-draft.json --idempotency-key cli-example-001
```

See the [SDK quick starts](examples/sdks/README.md) for runnable .NET, Node.js,
Python, Java, PHP, and Rust examples. Complete installation, execution lifecycle,
streaming, cancellation, approval, artifact, observability, and error guides are
indexed in [SDK usage](docs/sdk-usage.md).

Open `http://localhost:8080/dashboard/` to inspect persisted executions, provider-route resilience, redacted timelines, quality, reported cost, latency, token usage, and memory hits. A connected empty state means that no execution matches the selected tenant and time range; it does not mean the API is broken. In the local development profile, enter the same `X-Tenant-Id` value used for execution requests. JWT deployments can provide a bearer token in the dashboard session; credentials are not persisted by the page.

Dashboard cost is the normalized value reported by the configured gateway. The Core contract does not assign it a currency, so it is not an Azure invoice or billing reconciliation. Configure gateway cost units consistently and use provider billing data for financial reporting.

The worker host processes durable executions and also enforces TTL, quota, archival, and deletion policies asynchronously:

```bash
dotnet run --project src/iRoute.Worker
```

For the PostgreSQL profile, `docker compose -f deploy/compose.yaml up --build` starts the API, execution/lifecycle worker, migration job, and database together.

The smallest container quick start runs a durable SQLite API and worker sharing one volume:

```bash
docker compose -f deploy/compose.sqlite.yaml up --build --wait
```

Production-shaped packaging, migration, Kubernetes, upgrade, and rollback guidance is in the [deployment guide](deploy/README.md) and [operations guide](docs/operations.md).

Useful endpoints:

- `GET /v1/executions/{executionId}`
- `GET /v1/executions/{executionId}/events?after=0`
- `POST /v1/executions/{executionId}/cancel`
- `POST /v1/executions/{executionId}/approvals`
- `GET /v1/executions/{executionId}/external-actions` and `POST /v1/executions/{executionId}/external-actions/{actionId}/reconcile`
- `GET /v1/artifacts/{artifactId}`
- `GET /v1/observability/summary?from=...&to=...&taskType=...&policyVersion=...`
- `GET /v1/observability/executions/{executionId}`
- `GET /dashboard/`
- `GET /health/live`, `GET /health/ready`, `GET /health/model-gateway`, and `GET /openapi/v1.json`

## Verification

```bash
dotnet run --project tests/iRoute.UnitTests --no-build -- -reporter quiet
dotnet run --project tests/iRoute.ArchitectureTests --no-build -- -reporter quiet
npm run test:contracts
npm run test:deployment
npm run test:regression
npm run test:sdks
npm run test:release
```

With the API running, execute the initial behavioral evaluation fixture:

```bash
node tools/run-evaluation.mjs
```

The offline W10 gate needs no API or provider. `npm run test:regression` validates 42 candidate cases across all built-in tasks, compares the task-aware policy with the full-history single-strong reference, and verifies the checked reports under `eval/reports`. See [the evaluation guide](eval/README.md).

To exercise `ModelGateway__Mode=Http` without a provider dependency, start `node tools/gateway-conformance-server.mjs`, point the API at `http://127.0.0.1:5092`, and run the same evaluation. Set `ModelGateway__Transport=Streaming` to use bounded NDJSON streaming. `node tools/check-gateway-contract.mjs` verifies an external endpoint directly; run it against separate buffered and streaming gateways to prove contract parity. Gateway and normalized capability request/result/failure schemas are under `spec/schemas`.

The same sanitized server can exercise W18 locally. Run separate instances by setting `IROUTE_GATEWAY_PORT` and `IROUTE_GATEWAY_ID`; set `IROUTE_GATEWAY_FAILURE_STATUS=429`, `IROUTE_GATEWAY_RETRY_AFTER_SECONDS=30`, and optionally `IROUTE_GATEWAY_FAILURE_COUNT=1` on the primary. Register both loopback URLs under `ModelGateway__Deployments__0` and `__1`, then inspect the execution SSE/timeline and observability summary for circuit, attempt, and fallback evidence. This harness contains no provider SDK, credential, or Azure-specific assumption.

## Configuration

Use environment variables or standard ASP.NET Core configuration.

| Setting | Purpose | Default |
|---|---|---|
| `Storage__Provider` | `Postgres` to deploy, or `Sqlite` for single-node development | `Sqlite` |
| `Storage__AutoInitialize` | Create the prototype schema at startup | `true` |
| `ConnectionStrings__iRoute` | Durable database connection. A relative SQLite path resolves under the shared per-user data directory | `Data Source=iroute.db` |
| `ModelGateway__Mode` | `Deterministic` or `Http` | `Deterministic` |
| `ModelGateway__GatewayId` | Stable operator-defined external gateway identity | `external` |
| `ModelGateway__Transport` | `Buffered` or bounded NDJSON `Streaming` | `Buffered` |
| `ModelGateway__BaseUrl` | Generic HTTP gateway base URL | unset |
| `ModelGateway__ApiKey` | Generic HTTP gateway bearer credential | unset |
| `ModelGateway__Resilience__MaximumAttempts` | Absolute cross-deployment attempt ceiling, further bounded by the task | `3` |
| `ModelGateway__Resilience__Circuit__FailureThreshold` | Counted failures before a deployment circuit opens | `3` |
| `ModelGateway__Resilience__Circuit__OpenDuration` | Initial circuit-open interval | `30 seconds` |
| `ModelGateway__Resilience__Circuit__MaximumOpenDuration` | Maximum exponential/Retry-After circuit-open interval | `5 minutes` |
| `ModelGateway__Resilience__Circuit__ProbeLeaseDuration` | Fenced half-open probe lease | `15 seconds` |
| `Workflow__QueueCapacity` | Maximum queued ready steps per scheduling round | `16` |
| `Workflow__MaxParallelSteps` | Runtime ceiling on parallel steps per execution | `4` |
| `Workflow__RetryBaseDelayMilliseconds` | Initial classified-retry delay | `100` |
| `Workflow__RetryMaxDelayMilliseconds` | Maximum retry and `Retry-After` delay | `5,000` |
| `Workflow__RetryJitterRatio` | Deterministic retry jitter ratio | `0.2` |
| `ExecutionWorker__LeaseDuration` | Distributed execution lease duration | `30 seconds` |
| `ExecutionWorker__HeartbeatInterval` | Lease renewal and cancellation polling interval | `5 seconds` |
| `ExecutionWorker__MaxDeliveryAttempts` | Deliveries before a repeatedly failing execution is failed terminally; `0` redelivers forever | `5` |
| `ExecutionWorker__MaxAbandonDelay` | Ceiling on the redelivery delay, which doubles per failed delivery | `5 minutes` |
| `Lifecycle__SweepInterval` | Delay between lifecycle worker sweeps | `5 minutes` |
| `Lifecycle__DefaultArtifactTimeToLive` | Default lifetime assigned to new artifacts | `30 days` |
| `Lifecycle__DefaultMemoryTimeToLive` | Default lifetime assigned to new memory records | `90 days` |
| `Lifecycle__ArchiveAfterInactive` | Cold period before inactive state becomes archive-eligible | `7 days` |
| `Lifecycle__DeleteAfterArchive` | Minimum delay between archive and source deletion | `7 days` |
| `Lifecycle__ArchiveRetention` | Retention after the source has been deleted | `30 days` |
| `Lifecycle__MaxArtifactVersionsPerLineage` | Retained artifact versions per logical lineage | `5` |
| `Lifecycle__MaxMemoryVersionsPerLineage` | Retained memory versions per logical lineage | `5` |
| `Lifecycle__MaxArtifactsPerTenant` | Artifact-record quota used for cold candidate selection | `10,000` |
| `Lifecycle__MaxMemoryRecordsPerTenant` | Memory-record quota used for cold candidate selection | `10,000` |
| `Lifecycle__MaxArchivesPerTenant` | Retained archive-record quota | `20,000` |
| `Lifecycle__BatchSize` | Maximum expiry/archive/delete work per sweep stage | `500` |
| `Identity__Mode` | `DevelopmentHeaders` or `Jwt` | `DevelopmentHeaders` in Development; startup is rejected elsewhere |
| `Identity__Authority` | OpenID Connect/JWT issuer | required in JWT mode |
| `Identity__Audience` | Expected JWT audience | required in JWT mode |
| `Identity__TenantClaim` | Claim containing tenant identity | `tenant_id` |
| `Identity__ActorClaim` | Claim containing actor identity | `sub` |
| `Identity__PermissionClaim` | Claim containing space- or comma-separated permission scopes | `scope` |
| `Observability__PayloadMode` | `MetadataOnly` event envelopes or opt-in recursively `Redacted` fields | `MetadataOnly` |
| `Observability__MaxQueryDays` | Maximum requested summary window | `90` |
| `Observability__MaxExecutions` | Maximum executions projected per summary | `1,000` |
| `Observability__MaxTimelineEvents` | Maximum events returned for one timeline | `1,000` |
| `Observability__MaxEventValueCharacters` | Maximum retained safe string length | `1,000` |
| `Observability__MaxRecentExecutions` | Maximum recent execution rows in a summary | `25` |
| `OTEL_EXPORTER_OTLP_ENDPOINT` | Opt-in telemetry export | unset |

Use the deterministic gateway only for local development and repeatable tests. Buffered HTTP mode expects `POST {BaseUrl}/v1/execute`; streaming mode expects `POST {BaseUrl}/v1/stream` with monotonic NDJSON events and one terminal result. Both use the same provider-neutral request/result contract. `GET {BaseUrl}/health` supplies normalized optional-gateway health without affecting `/health/ready`.

For resilient HTTP routing, configure `ModelGateway__Deployments__0__...`, `ModelGateway__Deployments__1__...`, and so on. Each route declares a unique route/deployment ID, generic gateway URL and credential, provider/region/residency/model-version metadata, capabilities/profiles, expected quality/cost/latency, and priority. iRoute uses those fields for deterministic eligibility and observability; provider-specific request/response protocols remain behind the generic gateway. Configure the same list for every worker replica and use PostgreSQL for shared circuit state.

`DevelopmentHeaders` trusts `X-Tenant-Id`, `X-Actor-Id`, and `X-Permission-Scopes`. The API rejects this mode at startup unless the host environment is exactly `Development`. JWT mode is mandatory outside Development, requires an authenticated token with the configured tenant claim, and obtains permission scopes only from `Identity__PermissionClaim`; request headers and body fields cannot elevate them.

## Architecture and source of truth

The dependency rule is `Contracts <- Core <- Runtime <- Infrastructure <- Hosts`; SDKs depend only on the public protocol. See [the architecture guide](docs/architecture.md), [operations guide](docs/operations.md), [contract versioning rules](docs/contract-versioning.md), [SSE event contract](spec/events/sse-v1.md), [error taxonomy](spec/errors/error-taxonomy.v1.md), and [canonical product/engineering specification](docs/iRoute-Product-Engineering-Specification.md).

Public language-neutral contracts live in [OpenAPI](spec/openapi/iroute.v1.yaml) and [JSON Schema](spec/schemas). The [documentation map](docs/README.md) links the canonical Markdown sources. Adoption and maintenance are governed by the [compatibility promise](docs/compatibility.md), [contributor guide](CONTRIBUTING.md), [security policy](SECURITY.md), and [release process](docs/releasing.md). iRoute Core, contracts, official SDKs, and self-hosting remain Apache 2.0.
