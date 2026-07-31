# Operations baseline

## Health

- `/health/live` reports process liveness without probing dependencies.
- `/health/ready` probes the configured durable database when SQLite or PostgreSQL is active.
- The model gateway is intentionally not a readiness dependency; an unavailable optional capability should fail its execution, not remove the API from service.

## Storage profiles

The local API defaults to SQLite at `Data Source=iroute.db`. The Compose profile uses PostgreSQL. `Storage:AutoInitialize=true` applies checked-in migrations at startup. Disable automatic initialization where migrations are run as a separate deployment job. Every future migration requires upgrade, rollback, and mixed-version tests.

Workflow plans and step checkpoints are stored in `WorkflowPlans` and `WorkflowSteps`. Approvals and idempotent external-action reservations are stored in `Approvals` and `ExternalActions`. Versioned project facts/decisions are stored in `MemoryRecords`; artifact and memory provenance is normalized in `DependencyEdges`. A restart resets only interrupted workflow steps to `Pending`; completed step outputs and active project state remain authoritative inputs for downstream steps.

## Artifact and memory lifecycle

Artifact and memory lineages have exactly one active version. Materializing identical content returns that version; changed content creates the next version and marks the prior version superseded. When a referenced memory/source version changes or disappears, targeted invalidation marks active dependents invalid and follows artifact-to-artifact edges recursively. Operators can inspect lifecycle metadata and hashes without loading request, memory, or artifact payloads into events or telemetry.

All direct reads and invalidation queries require a tenant scope at the persistence boundary. Never implement an administrative cleanup or repair job with an unscoped artifact, memory, or dependency query. PostgreSQL serializable transactions protect version allocation; retry serialization conflicts at the job boundary rather than inventing a version.

## Scheduler bounds

`Workflow:QueueCapacity` bounds ready steps waiting inside one scheduling round. `Workflow:MaxParallelSteps` is the runtime-wide ceiling applied in addition to the lower per-plan parallel-call budget. Queue writers wait when capacity is full, so load produces backpressure rather than unbounded in-memory growth.

## Identity

`Identity:Mode=DevelopmentHeaders` is intended only for local development. Internet-facing deployments must use `Jwt`, configure an HTTPS authority and audience, and issue tokens containing the configured tenant, actor, and permission claims. The API replaces caller-supplied request scopes with authenticated scopes before policy evaluation. External-action approval requires both the action scope (for example `email:send`) and `approval:grant`.

## External-action safety and recovery

An external write requires explicit request intent, a tenant-scoped idempotency key, an allowed task capability, its configured permission scopes, and an approved durable action record. Events persist the policy version, actor, decision, and input/result references; they never intentionally persist payload bodies.

A completed external action is replayed from its durable result. A conflicting reference is rejected. If the process loses certainty after reserving or starting an action, the reservation remains `Running` and the runtime returns `external_action_in_progress` rather than invoking it again. Operators must reconcile the provider using the stored idempotency reference. A future administrative workflow will support repairing the durable action state; automatic distributed reconciliation is not part of W04.

## Scaling

The current HTTP profile executes synchronously. The dependency scheduler persists every attempt and can resume an interrupted plan without repeating completed steps. API replicas may share PostgreSQL for reads and persisted outcomes, but in-flight cancellation is signalled only inside the process executing the request. Cross-replica leasing, renewal, automatic recovery scans, external-action reconciliation, and distributed cancellation remain required before horizontal execution scaling.

## Telemetry

ASP.NET Core, HTTP client, and runtime metrics/traces are instrumented. Export is disabled unless `OTEL_EXPORTER_OTLP_ENDPOINT` is configured. Request and artifact payloads are not intentionally added to telemetry.

## Backup and recovery

Production requires PostgreSQL point-in-time recovery, object-store versioning when object storage is introduced, restoration drills, and documented recovery objectives. A backup is not accepted until a restore has been tested.

## Upgrade and privacy

Schema changes must use expand-and-contract migrations. API and worker versions must overlap during rolling upgrades. Retention, deletion, and export must remain tenant-scoped and eventually cover indexes, artifacts, memory, and evaluation samples.
