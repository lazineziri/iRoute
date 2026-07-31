# Operations baseline

## Health

- `/health/live` reports process liveness without probing dependencies.
- `/health/ready` probes the configured durable database when SQLite or PostgreSQL is active.
- `/health/model-gateway` probes the configured gateway and returns its normalized identity, state, observed probe latency, timestamp, and safe message.
- The model gateway is intentionally not a readiness dependency; an unavailable optional capability returns HTTP 503 from its dedicated health endpoint and fails its execution without removing the API from service.

## Storage profiles

The local API defaults to SQLite at `Data Source=iroute.db`. The Compose profile uses PostgreSQL. `Storage:AutoInitialize=true` applies checked-in migrations at startup. Disable automatic initialization where migrations are run as a separate deployment job. Every future migration requires upgrade, rollback, and mixed-version tests.

Workflow plans, their routing decisions, and step checkpoints are stored in `WorkflowPlans` and `WorkflowSteps`. Approvals and idempotent external-action reservations are stored in `Approvals` and `ExternalActions`. Versioned project facts/decisions are stored in `MemoryRecords`; artifact and memory provenance is normalized in `DependencyEdges`. A restart resets only interrupted workflow steps to `Pending`; completed step outputs, the selected route/profile, and active project state remain authoritative inputs for downstream steps.

## Artifact and memory lifecycle

Artifact and memory lineages have exactly one active version. Materializing identical content returns that version; changed content creates the next version and marks the prior version superseded. When a referenced memory/source version changes or disappears, targeted invalidation marks active dependents invalid and follows artifact-to-artifact edges recursively. Operators can inspect lifecycle metadata and hashes without loading request, memory, or artifact payloads into events or telemetry.

All direct reads and invalidation queries require a tenant scope at the persistence boundary. Never implement an administrative cleanup or repair job with an unscoped artifact, memory, or dependency query. PostgreSQL serializable transactions protect version allocation; retry serialization conflicts at the job boundary rather than inventing a version.

## No-model resolution audit

The runtime checks resolvers in this order: `exact-cache`, `fact-decision`, `artifact-lookup`, then `deterministic-handler`. A resolver must return a structured acceptance or rejection; silent misses are not allowed. `resolution.considered` records the stable decision code, safe reason, permission/freshness flags, check count, and candidate level. It must never contain memory values, artifact bodies, prompts, or handler outputs.

Fact and decision tasks require `project:read`. Explicit artifacts must match the authenticated tenant and requested project as well as the current task definition and artifact type. Deterministic handlers are eligible only when their capability is allow-listed by the task definition. Semantic matching is disabled by default; operators must not introduce a shared embedding index without tenant isolation, freshness propagation, and evaluation-backed thresholds.

## Context compilation audit

`context.compiled` reports the task budget, final serialized token estimate, truncation state, full-history flag, entry count, included count, and provenance count. The corresponding outcome manifest records every included and excluded candidate with rank and reason. Included entries must have an `outputPath`, and that path must exist in `provenance` with a non-empty source reference.

Raw `projectHistory` is removed from the model task input and never passed through wholesale. The compiler admits at most three recent relevant history candidates and can exclude fewer when higher-ranked evidence or the token budget is sufficient. `estimatedTokens` equals `projectedInputTokens + contextTokens`; if the projected task input cannot fit by itself, execution fails with `context_budget_exceeded` before the model gateway runs. Project memory queries must remain tenant/project scoped and active/fresh. Artifact context must be explicitly requested through `contextArtifacts`; never replace that allow-list with an unbounded project-artifact scan.

## Scheduler bounds

`Workflow:QueueCapacity` bounds ready steps waiting inside one scheduling round. `Workflow:MaxParallelSteps` is the runtime-wide ceiling applied in addition to the lower per-plan parallel-call budget. Queue writers wait when capacity is full, so load produces backpressure rather than unbounded in-memory growth.

## Routing audit

The current routing policy is `routing.w08.v1`. For single-capability tasks the direct selector must report `plannerInvoked=false` and `planningCalls=0`. Multi-capability definitions invoke the deterministic bounded planner once; it must fail with `routing_budget_exceeded` before checkpointing if required depth or calls exceed the lower request/task-definition ceiling.

Model profiles are evaluation-derived measurements, not provider marketing names. A candidate is eligible only when its task coverage, health, quality, deadline, token capacity, cost, model-call budget, and capability allow list all pass. Operators should compare `routing.decided` candidate measurements with actual `gateway.completed` usage. An unexplained profile change, a missing candidate reason, or routing below the quality floor is an incident. Profile edits require the evaluation and contract suites; do not raise request limits or quality estimates dynamically in production.

## Model-gateway operations

`ModelGateway:Mode=Http` uses only the generic W09 contract. Buffered mode calls `POST v1/execute`; streaming mode calls `POST v1/stream` and consumes monotonic `application/x-ndjson` events with a maximum of 10,000 events and 65,536 characters per line. A stream is valid only when it ends with exactly one completed result and emits nothing afterward. The request includes the selected capability/profile, maximum output tokens, correlation ID, and effective step deadline. Cancellation is passed directly to the HTTP request and response reader.

The runtime treats its own wall-clock duration as authoritative latency and normalizes every successful model call to at least one model invocation. Negative usage, confidence outside zero-to-one, missing output/evidence, malformed JSON, non-monotonic stream sequences, missing completion, or post-completion data fail with `model_gateway_invalid_response`. `gateway.started`, `gateway.streamed`, `gateway.completed`, and `gateway.failed` expose identities, counts, normalized usage, classifications, and latency only; prompts, context, deltas, outputs, credentials, and provider response bodies must never enter audit events.

HTTP failures are classified as invalid request, authentication, timeout, rate limit, unavailable, or internal. Only timeout, rate-limit, and unavailable failures are retryable. The gateway remains responsible for provider credentials, provider model aliases, provider protocols, and provider health. Do not add provider model names, request fields, or response parsing to Contracts, Core, Runtime, routing policy, or task definitions.

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
