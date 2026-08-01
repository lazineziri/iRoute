# Operations baseline

## Health

- `/health/live` reports process liveness without probing dependencies.
- `/health/ready` probes the configured durable database when SQLite or PostgreSQL is active.
- `/health/model-gateway` probes the configured gateway and returns its normalized identity, state, observed probe latency, timestamp, and safe message.
- The model gateway is intentionally not a readiness dependency; an unavailable optional capability returns HTTP 503 from its dedicated health endpoint and fails its execution without removing the API from service.

## Storage profiles

The local API defaults to SQLite at `Data Source=iroute.db`. The Compose profile uses PostgreSQL. `Storage:AutoInitialize=true` applies checked-in migrations at startup. Disable automatic initialization where migrations are run as a separate deployment job. Every future migration requires upgrade, rollback, and mixed-version tests.

Workflow plans, their routing decisions, and step checkpoints are stored in `WorkflowPlans` and `WorkflowSteps`. Approvals and idempotent external-action reservations are stored in `Approvals` and `ExternalActions`. Versioned project facts/decisions are stored in `MemoryRecords`; artifact and memory provenance is normalized in `DependencyEdges`; cold lifecycle payloads are stored in `LifecycleArchives`. A restart resets only interrupted workflow steps to `Pending`; completed step outputs, the selected route/profile, and active project state remain authoritative inputs for downstream work, while completed archives preserve recoverable provenance.

## Artifact and memory lifecycle

Artifact and memory lineages have exactly one active version. Materializing identical content returns that version; changed content creates the next version and marks the prior version superseded. When a referenced memory/source version changes or disappears, targeted invalidation marks active dependents invalid and follows artifact-to-artifact edges recursively. Operators can inspect lifecycle metadata and hashes without loading request, memory, or artifact payloads into events or telemetry.

New records without an explicit expiry receive `Lifecycle:DefaultArtifactTimeToLive` or `Lifecycle:DefaultMemoryTimeToLive`. Each sweep expires due active records first and propagates invalidation before selecting cold records. Candidates come from inactive age, per-lineage overflow, and tenant overflow. `BatchSize` bounds each stage. A record with an active artifact or memory dependent is protected even when it exceeds a quota.

Archival and deletion are deliberately separate phases. A sweep first writes a tenant-scoped archive containing the source entity, dependency references, and content hash. Only an archive that existed before the current sweep and is older than `DeleteAfterArchive` can authorize physical source deletion. Deletion removes incoming and outgoing dependency edges and repairs supersession pointers. The archive remains until the source is gone and either `ArchiveRetention` elapses or the tenant archive quota requires oldest-first removal. `DanglingDependencyEdgeCount` must remain zero after every sweep.

Run `src/iRoute.Worker` continuously for durable profiles; the Compose profile starts it beside the API. Until distributed worker leasing is implemented, run one lifecycle worker per database. Sweep completion logs expose only counts—expired, archived, deleted, protected, purged, remaining records, and dangling edges—not archived payloads. A failed sweep rolls back its durable transaction and is retried on the next interval.

All direct reads, archives, deletions, and invalidation queries require a tenant scope at the persistence boundary. Never implement an administrative cleanup or repair job with an unscoped artifact, memory, archive, or dependency query. PostgreSQL serializable transactions protect version allocation and lifecycle mutation; treat serialization conflicts as retryable job failures rather than inventing a version. Keep API TTL values aligned with worker values so newly written expiry timestamps match the operating policy.

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

## Capability connector operations

Every connector is selected by a versioned capability definition and must have exactly one registered implementation. The normalized executor checks side-effect class and authenticated scopes again at the connector boundary, enforces the step deadline and output-byte limit, and returns only a projected result with evidence, confidence, usage, trust level, transport, connector identity, and a SHA-256 output reference. `capability.started`, `capability.completed`, and `capability.failed` are the safe audit surface; do not add connector input, raw output, credentials, authorization headers, email bodies, database rows beyond the approved projection, MCP instructions, or agent scratch data to those events.

Database connectors accept only registered query identifiers and must add tenant filters, row limits, and timeouts in the real adapter. OpenAPI connectors accept only registered operation identifiers with fixed host, method, path, credentials, side-effect class, and response projection. MCP connectors require registered server/tool pairs and treat returned content as untrusted data. Agent results require schema, provenance, freshness, dependency, and policy validation. The deterministic adapters prove these invariants but are not production integrations.

Read-only steps may execute immediately after task policy succeeds. Reversible and irreversible writes must continue through explicit write intent, required scopes, durable approval where configured, and an idempotent external-action reservation. Never register a write capability as `None` or `ReadOnly` to bypass this path. An unknown capability, duplicate connector registration, side-effect mismatch, invalid projection, oversized output, or expired deadline fails closed with a classified capability error.

## Evaluation regression gate

Run `npm run test:regression` for every routing-policy or model-profile change. CI validates the golden dataset and generated result/report contracts, requires all six scenario categories for every task discovered in the built-in registry, evaluates the baseline and candidate observations, and compares the generated output byte-for-byte with the checked JSON and Markdown reports.

The candidate policy source fingerprint covers `RoutingAndPlanning.cs` and the built-in task/model-profile registry. A mismatch is intentional fail-closed behavior: record fresh observations, update the dataset fingerprint, inspect quality/safety/cost/latency deltas, then run `npm run eval:write`. Do not regenerate reports without updating observations from a real evaluation run. The committed benchmark inputs support deterministic regression; they are not production latency or cost SLOs. Keep environment-specific measurements outside the repository when they contain customer data, credentials, provider payloads, or proprietary pricing.

A policy is releasable only when every candidate case reaches a completed terminal result, meets its task quality floor, produces no unsupported claims or unsafe actions, and does not increase per-task cost or latency without at least the configured justified quality gain. The live `node tools/run-evaluation.mjs` suite remains required for runtime, persistence, and external-gateway behavior that an offline replay cannot prove.

## Identity

`Identity:Mode=DevelopmentHeaders` is intended only for local development. Internet-facing deployments must use `Jwt`, configure an HTTPS authority and audience, and issue tokens containing the configured tenant, actor, and permission claims. The API replaces caller-supplied request scopes with authenticated scopes before policy evaluation. External-action approval requires both the action scope (for example `email:send`) and `approval:grant`.

## External-action safety and recovery

An external write requires explicit request intent, a tenant-scoped idempotency key, an allowed task capability, its configured permission scopes, and an approved durable action record. Events persist the policy version, actor, decision, and input/result references; they never intentionally persist payload bodies.

A completed external action is replayed from its durable result. A conflicting reference is rejected. If the process loses certainty after reserving or starting an action, the reservation remains `Running` and the runtime returns `external_action_in_progress` rather than invoking it again. Operators must reconcile the provider using the stored idempotency reference. A future administrative workflow will support repairing the durable action state; automatic distributed reconciliation is not part of W04.

## Scaling

The current HTTP profile executes synchronously. The dependency scheduler persists every attempt and can resume an interrupted plan without repeating completed steps. API replicas may share PostgreSQL for reads and persisted outcomes, but in-flight cancellation is signalled only inside the process executing the request. The lifecycle host is asynchronous but does not yet own a distributed lease. Cross-replica leasing, renewal, automatic recovery scans, external-action reconciliation, and distributed cancellation remain required before horizontal execution scaling.

## Observability and telemetry

`GET /v1/observability/summary` returns tenant-scoped aggregates over a bounded time window and can filter by `taskType` and `policyVersion`. `GET /v1/observability/executions/{executionId}` returns an ordered timeline with its trace ID; a different tenant receives not found. The dashboard at `/dashboard/` calls only these authenticated data endpoints. Static assets are public, but no execution data is embedded in them.

The default limits are 90 query days, 1,000 sampled executions, 25 recent rows, 1,000 timeline events, and 1,000 characters per retained safe event string. A `truncated` flag identifies bounded results. Increase limits only after measuring database and response costs; the projection reads existing execution/event persistence and intentionally does not maintain a second analytics database.

`Observability:PayloadMode=MetadataOnly` is the default and replaces every event data object with a redaction marker, making unknown future event shapes fail closed. Operators may opt into `Redacted`, which removes input, output, content, value, prompt, response, request, body, raw, credential, secret, authorization, tenant/actor/project IDs, permission scopes, passwords, tokens, cookies, headers, and API-key variants recursively while bounding retained safe strings. Actor and project references are one-way SHA-256 prefixes. Never add payload, credential, scope values, raw identifiers, or unbounded strings to a trace tag, metric attribute, event, log, or dashboard field.

OpenTelemetry instruments ASP.NET Core, HTTP clients, runtime process metrics, and the custom `iRoute.Runtime` execution source/meter. Execution spans cover execute/resume, attach safe ordered event names, and record terminal quality, cost, tokens, latency, and call counts. Export remains disabled unless `OTEL_EXPORTER_OTLP_ENDPOINT` is configured. Correlate the span trace ID with the durable timeline when investigating a request; use task type and policy version—not tenant or actor—as comparison dimensions.

## Backup and recovery

Production requires PostgreSQL point-in-time recovery, object-store versioning when object storage is introduced, restoration drills, and documented recovery objectives. A backup is not accepted until a restore has been tested.

## Upgrade and privacy

Schema changes must use expand-and-contract migrations. API and worker versions must overlap during rolling upgrades. Retention, deletion, and export must remain tenant-scoped and eventually cover indexes, artifacts, memory, and evaluation samples.
