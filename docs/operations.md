# Operations

iRoute ships one binary and one image. Process mode, storage, identity, and
configuration determine whether it runs as a local all-in-one service or a
separated API/worker deployment.

## Process modes

| Command | Role |
|---|---|
| `iroute serve` | ASP.NET Core API, health, OpenAPI, dashboard, and optional embedded workers |
| `iroute worker` | Durable execution worker and lifecycle worker |
| `iroute migrate status` | Read migration state |
| `iroute migrate up [target]` | Apply migrations |
| `iroute migrate down <target> --confirm` | Explicit destructive rollback |

For SQLite, run one `serve` process with `Runtime__RunBackgroundWorkers=true`.
For PostgreSQL, disable embedded workers on API replicas and run separate worker
processes. Scale execution workers horizontally, but run one lifecycle-enabled
worker per database.

## Configuration

Configuration follows the standard .NET provider order. Environment variables
use double underscores for nested keys.

### Storage

```text
Storage__Provider=Sqlite|Postgres
Storage__AutoInitialize=true|false
ConnectionStrings__iRoute=<connection string>
```

SQLite is for local/single-process operation. Its relative file path resolves to
the iRoute per-user data directory. Use an absolute path or mounted
`/var/lib/iroute` path in containers.

PostgreSQL is required for multiple API/worker replicas. Set
`Storage__AutoInitialize=false` and run the migration command as a deployment
step. Every process must use the same database and configuration policy.

### Identity

`Identity__Mode=DevelopmentHeaders` is accepted only when
`ASPNETCORE_ENVIRONMENT=Development`. It trusts `X-Tenant-Id`, `X-Actor-Id`, and
`X-Permission-Scopes`; bind only to loopback.

Production uses:

```text
Identity__Mode=Jwt
Identity__Authority=https://issuer.example
Identity__Audience=iroute
Identity__TenantClaim=tenant_id
Identity__ActorClaim=sub
Identity__PermissionClaim=scope
```

Startup fails if JWT authority or audience is missing. Tenant and actor are
derived from claims and request payloads cannot override them.

### Execution worker

Tenant admission, fair dispatch, request/body limits, and their configuration are
documented in [tenant quotas](tenant-quotas.md). Quota enforcement is opt-in;
do not mistake local HTTP rate limits for a distributed billing ceiling.

The worker leases queued executions from storage, renews ownership, observes
distributed cancellation, and resumes from durable checkpoints. Important keys
are `ExecutionWorker__Enabled`, `PollInterval`, `LeaseDuration`,
`HeartbeatInterval`, and `AbandonDelay`. Heartbeat must be materially shorter
than lease duration. Do not run workers with clocks that are not synchronized.

### Workflow

`Workflow__QueueCapacity` and `MaxParallelSteps` bound one execution scheduling
round. Retry settings apply only to classified workflow/capability failures:
`RetryBaseDelayMilliseconds`, `RetryMaxDelayMilliseconds`, and
`RetryJitterRatio`. Model-deployment fallback is owned by gateway resilience and
must not be duplicated by generic HTTP retry middleware.

### Model gateway

Development defaults to `ModelGateway__Mode=Deterministic` and needs no key.
HTTP mode uses the provider-neutral gateway contract:

```text
ModelGateway__Mode=Http
ModelGateway__Transport=Buffered|Streaming
ModelGateway__BaseUrl=https://gateway.example
ModelGateway__ApiKey=<secret>
ModelGateway__GatewayId=external
```

For deterministic fallback, register ordered entries under
`ModelGateway__Deployments__{index}`. Each entry declares stable route,
deployment, gateway, provider, region/residency, model version, capabilities,
profiles, expected quality/cost/latency, priority, transport, URL, and secret.
Use identical registration on every worker.

Circuit behavior is controlled by `ModelGateway__Resilience`: maximum attempts,
failure threshold, initial/max open duration, and half-open probe lease. Circuit
state is shared in PostgreSQL. A provider `Retry-After` may extend the open
interval. Secrets and raw provider bodies are never written to execution events.

`OpenAI` and `Anthropic` modes call the native provider APIs with `Model` and
their own keys. Deployment entries select the implementation with `Adapter`.
`ClaudeCode` and `OpenAIChatGPT` are owner-tenant, Development-only subscription
routes and cannot silently fall back to API billing. ChatGPT native sign-in and
refresh use iRoute's own owner-only macOS/Linux credential files; never mount
another app's credentials. See [model providers](model-providers.md) for setup,
token lifecycle limits, unknown-cost handling, and unprobed health semantics.

Approval decisions require a separate actor, every task permission scope, and
`approval:grant`. Even an actor with those scopes cannot decide their own
proposal. Reconciliation also requires `approval:grant` and remains tenant-scoped.

### Lifecycle and observability

`Lifecycle` bounds artifact/memory TTL, inactivity before archive, source
deletion, archive retention, lineage versions, tenant record counts, batch size,
and sweep interval. Review those values against backup and legal-retention needs
before production use.

`Observability__PayloadMode=MetadataOnly` is the safe default. Query window,
execution count, timeline event count, string size, and recent-row limits prevent
the dashboard from becoming an unbounded export. `Redacted` mode remains an
explicit opt-in and still recursively removes configured sensitive values.

Set `OTEL_EXPORTER_OTLP_ENDPOINT` to export traces and metrics. iRoute records
low-cardinality execution, routing, quality, latency, token, reported-cost,
memory-hit, and gateway/circuit signals. Gateway-reported cost is not a billing
reconciliation unless the operator standardizes that unit.

## Health and diagnostics

| Endpoint | Meaning |
|---|---|
| `/health/live` | Process is alive; no dependency checks |
| `/health/ready` | Registered readiness checks pass |
| `/health/model-gateway` | Current gateway health; returns 503 when unavailable |
| `/openapi/v1.json` | Generated runtime OpenAPI document |
| `/dashboard/` | Static operator UI backed by tenant-scoped observability APIs |

On startup failure, first inspect options-validation errors. Then verify the
database connection, migration state, JWT authority/audience, gateway route
registration, and secret injection. For a container:

```bash
docker logs <container>
docker inspect <container> --format '{{json .State.Health}}'
```

For Kubernetes:

```bash
kubectl -n iroute get pods
kubectl -n iroute describe pod <pod>
kubectl -n iroute logs <pod> --all-containers
```

## Migration and rollout

1. Back up the database and confirm restore procedures.
2. Run the new image as `migrate status`.
3. Run one short-lived `migrate up` job.
4. Roll API replicas with embedded workers disabled.
5. Roll execution workers.
6. Keep exactly one lifecycle worker active.
7. Verify readiness, submit a canary task, observe its terminal event, and check
   the dashboard/telemetry.

Do not let every replica auto-initialize production storage. The checked Compose
profile encodes the migration-before-start ordering; the Kubernetes migration
Job is intentionally applied separately before the Kustomize workload rollout.

## Rollback

Application rollback is preferred: stop the rollout and restore the previous
image while leaving an additive schema in place. Confirm that the previous
version is within the documented compatibility window.

Use `migrate down <target> --confirm` only when the migration is documented as
reversible, a current backup exists, and data loss is understood. Never run a
rollback concurrently with active API or worker writes.

## Backups and recovery

- Back up PostgreSQL using the platform's consistent snapshot/PITR facility.
- Back up SQLite only while its writer is stopped or via a SQLite-aware backup.
- Restore into an isolated environment, run `migrate status`, and execute a
  tenant-scoped canary before redirecting traffic.
- Preserve release version, image digest, configuration, migration output, and
  checksum manifest with every production change.

## Production gaps

The current alpha includes experimentally tested quota reservation and fair
dispatch, not production-scale guarantees. It does not claim production-grade
connector adapters, sustained load/soak limits, multi-region failover, or an
operational SLA. Treat reference connectors and deterministic model profiles
as development fixtures until measured evidence is registered.
