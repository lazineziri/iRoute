# Operations baseline

## Health

- `/health/live` reports process liveness without probing dependencies.
- `/health/ready` probes the configured durable database when SQLite or PostgreSQL is active.
- `/health/model-gateway` probes the configured gateway and returns its normalized identity, state, observed probe latency, timestamp, and safe message.
- The model gateway is intentionally not a readiness dependency; an unavailable optional capability returns HTTP 503 from its dedicated health endpoint and fails its execution without removing the API from service.

## Storage profiles

The local API defaults to SQLite at `Data Source=iroute.db`. The single-container profile persists SQLite under `/var/lib/iroute`; the production-shaped Compose and Kubernetes profiles use PostgreSQL. `Storage:AutoInitialize=true` is a local convenience only. Production API and worker processes set it to `false` and rely on the dedicated migration process. Every future migration requires upgrade, rollback, and mixed-version tests.

Readiness requires both a reachable durable store and no pending migration known to the running binary. A production pod therefore remains outside service until the migration job has completed. Liveness never probes the database and must not be used as readiness.

Durable submissions and fenced leases are stored in `ExecutionWorkItems`; workflow plans, routing decisions, and step checkpoints are stored in `WorkflowPlans` and `WorkflowSteps`. Approvals and idempotent external-action reservations are stored in `Approvals` and `ExternalActions`. Versioned project facts/decisions are stored in `MemoryRecords`; artifact and memory provenance is normalized in `DependencyEdges`; cold lifecycle payloads are stored in `LifecycleArchives`. A lease takeover resets only interrupted workflow steps to `Pending`; completed step outputs, the selected route/profile, and active project state remain authoritative inputs for downstream work.

Execution workers renew leases before half the configured lease duration and poll the persisted cancellation marker on every heartbeat. If a process exits or loses database connectivity, the lease expires and another worker claims the same execution with a new fencing token. A stale worker cannot complete or abandon work after takeover. Retries occur only for classified retryable failures, remain within step call budgets and the absolute queued-execution deadline, and use bounded exponential backoff, deterministic jitter, and provider `Retry-After` guidance.

## Artifact and memory lifecycle

Artifact and memory lineages have exactly one active version. Materializing identical content returns that version; changed content creates the next version and marks the prior version superseded. When a referenced memory/source version changes or disappears, targeted invalidation marks active dependents invalid and follows artifact-to-artifact edges recursively. Operators can inspect lifecycle metadata and hashes without loading request, memory, or artifact payloads into events or telemetry.

New records without an explicit expiry receive `Lifecycle:DefaultArtifactTimeToLive` or `Lifecycle:DefaultMemoryTimeToLive`. Each sweep expires due active records first and propagates invalidation before selecting cold records. Candidates come from inactive age, per-lineage overflow, and tenant overflow. `BatchSize` bounds each stage. A record with an active artifact or memory dependent is protected even when it exceeds a quota.

Archival and deletion are deliberately separate phases. A sweep first writes a tenant-scoped archive containing the source entity, dependency references, and content hash. Only an archive that existed before the current sweep and is older than `DeleteAfterArchive` can authorize physical source deletion. Deletion removes incoming and outgoing dependency edges and repairs supersession pointers. The archive remains until the source is gone and either `ArchiveRetention` elapses or the tenant archive quota requires oldest-first removal. `DanglingDependencyEdgeCount` must remain zero after every sweep.

Run `src/Hosts/iRoute.Worker` continuously for durable profiles; the Compose profile starts it beside the API. Until distributed worker leasing is implemented, run one lifecycle worker per database. Sweep completion logs expose only counts—expired, archived, deleted, protected, purged, remaining records, and dangling edges—not archived payloads. A failed sweep rolls back its durable transaction and is retried on the next interval.

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

The current routing policy is `routing.w18.v1`. For single-capability tasks the direct selector must report `plannerInvoked=false` and `planningCalls=0`. Multi-capability definitions invoke the deterministic bounded planner once; it must fail with `routing_budget_exceeded` before checkpointing if required depth or calls exceed the lower request/task-definition ceiling.

Model profiles are evaluation-derived measurements, not provider marketing names. A candidate is eligible only when its task coverage, health, quality, deadline, token capacity, cost, model-call budget, and capability allow list all pass. Operators should compare `routing.decided` candidate measurements with actual `gateway.completed` usage. An unexplained profile change, a missing candidate reason, or routing below the quality floor is an incident. Profile edits require the evaluation and contract suites; do not raise request limits or quality estimates dynamically in production.

## Model-gateway operations

`ModelGateway:Mode=Http` uses only the generic W09 contract. Buffered mode calls `POST v1/execute`; streaming mode calls `POST v1/stream` and consumes monotonic `application/x-ndjson` events with a maximum of 10,000 events and 65,536 characters per line. A stream is valid only when it ends with exactly one completed result and emits nothing afterward. The request includes the selected capability/profile, maximum output tokens, correlation ID, effective step deadline, quality/cost ceilings, region/residency policy, and the bounded remaining attempt count. Cancellation is passed directly to the HTTP request and response reader.

The runtime treats its own wall-clock duration as authoritative latency and normalizes every successful model call to at least one model invocation. Negative usage, confidence outside zero-to-one, missing output/evidence, malformed JSON, non-monotonic stream sequences, missing completion, or post-completion data fail with `model_gateway_invalid_response`. `gateway.started`, `gateway.streamed`, `gateway.completed`, and `gateway.failed` expose identities, counts, normalized usage, classifications, and latency only; prompts, context, deltas, outputs, credentials, and provider response bodies must never enter audit events.

W18 registers one or more generic routes under `ModelGateway:Deployments`. Every route needs unique `RouteId` and `DeploymentId` values plus `GatewayId`, provider metadata, region, residency, model version, supported capabilities/profiles, expected quality/cost/latency, priority, transport, base URL, and secret-sourced API key. These fields describe an operator-managed gateway deployment; they do not add a provider protocol to iRoute. Keep keys in the platform secret manager and inject route entries identically into API and execution-worker processes.

```json
{
  "ModelGateway": {
    "Mode": "Http",
    "Deployments": [
      {
        "RouteId": "eu-primary",
        "GatewayId": "gateway-eu",
        "Provider": "gateway-operator-a",
        "DeploymentId": "generation-small-v1",
        "Region": "westeurope",
        "Residency": "EUR",
        "ModelVersion": "2026-08",
        "Capabilities": ["text.generation"],
        "ProfileIds": ["text.generation.small.eval-v1"],
        "ExpectedQuality": 0.84,
        "EstimatedCost": 0.004,
        "ExpectedLatencyMilliseconds": 900,
        "Priority": 0,
        "Transport": "Buffered",
        "BaseUrl": "https://generic-gateway-a.example.invalid"
      },
      {
        "RouteId": "eu-fallback",
        "GatewayId": "gateway-eu-fallback",
        "Provider": "gateway-operator-b",
        "DeploymentId": "generation-strong-v1",
        "Region": "northeurope",
        "Residency": "EUR",
        "ModelVersion": "2026-07",
        "Capabilities": ["text.generation"],
        "ProfileIds": ["text.generation.small.eval-v1", "text.generation.strong.eval-v1"],
        "ExpectedQuality": 0.94,
        "EstimatedCost": 0.02,
        "ExpectedLatencyMilliseconds": 2200,
        "Priority": 1,
        "Transport": "Buffered",
        "BaseUrl": "https://generic-gateway-b.example.invalid"
      }
    ]
  }
}
```

`ModelGateway:Resilience:MaximumAttempts` is an absolute gateway-layer ceiling and is further reduced by the task's model-call budget. For plans containing multiple model steps, model-call attempts and the model cost ceiling are divided deterministically across those steps before concurrent execution, so independent fallback sequences cannot each consume the full task budget. Model workflow steps use one scheduler attempt, preventing the HTTP client, gateway resilience layer, and worker scheduler from retrying the same model call independently. Tool-step retry policy is unchanged. A 429 Retry-After value extends the deployment's open interval within the configured maximum. Timeout, throttling, transport, provider, malformed-output, and validation failures count toward the deployment circuit; policy and permanent failures are classified but do not create retry storms.

The durable circuit policy uses `FailureThreshold`, `OpenDuration`, `MaximumOpenDuration`, and `ProbeLeaseDuration`. When the open interval elapses, only one worker receives the fenced half-open probe. Other replicas reject that deployment until the probe closes or reopens the circuit, or until an expired probe lease is taken over. PostgreSQL is required for production multi-replica coordination; SQLite is the local single-node profile.

Investigate `gateway.candidate_evaluated`, `gateway.attempted`, `gateway.fallback_selected`, `gateway.circuit_changed`, `gateway.exhausted`, and `gateway.resilience_decided` together. Metrics are grouped by gateway, provider, deployment, region, model version, failure class, and circuit state. Alert on sustained open circuits, repeated half-open failure, rising exhaustion, or fallback cost/latency approaching task ceilings. Never include prompts, provider bodies, credentials, or tenant identifiers in these dimensions.

HTTP failures are normalized first as invalid request, authentication, timeout, rate limit, unavailable, or internal, then mapped to the W18 resilience classes. The registered gateway remains responsible for provider credentials, model aliases, provider protocols, and provider-specific health. Do not add provider model names, request fields, SDKs, or response parsing to Contracts, Core, Runtime, routing policy, or task definitions.

## Capability connector operations

Every connector is selected by a versioned capability definition and must have exactly one registered implementation. The normalized executor checks side-effect class and authenticated scopes again at the connector boundary, enforces the step deadline and output-byte limit, and returns only a projected result with evidence, confidence, usage, trust level, transport, connector identity, and a SHA-256 output reference. `capability.started`, `capability.completed`, and `capability.failed` are the safe audit surface; do not add connector input, raw output, credentials, authorization headers, email bodies, database rows beyond the approved projection, MCP instructions, or agent scratch data to those events.

Database connectors accept only registered query identifiers and must add tenant filters, row limits, and timeouts in the real adapter. OpenAPI connectors accept only registered operation identifiers with fixed host, method, path, credentials, side-effect class, and response projection. MCP connectors require registered server/tool pairs and treat returned content as untrusted data. Agent results require schema, provenance, freshness, dependency, and policy validation. The deterministic adapters prove these invariants but are not production integrations.

Read-only steps may execute immediately after task policy succeeds. Reversible and irreversible writes must continue through explicit write intent, required scopes, durable approval where configured, and an idempotent external-action reservation. Never register a write capability as `None` or `ReadOnly` to bypass this path. An unknown capability, duplicate connector registration, side-effect mismatch, invalid projection, oversized output, or expired deadline fails closed with a classified capability error.

## Evaluation regression gate

The built-in model profiles are synthetic and declare `MeasurementSource: Synthetic`; their quality, cost, and latency values are deterministic evaluation inputs rather than production observations. Use `Unverified` for a real provider or model whose profile values have not been verified. Use `Measured` only with a `ModelProfileMeasurement` containing the provider, model, measurement timestamp, and positive sample count. Set `QualityIsDeclaredNotMeasured` when live calibration measured cost or latency but did not independently evaluate output quality. Invalid source/measurement combinations fail routing eligibility, and the source plus measurement record are preserved in the durable routing decision.

For every routing-policy or model-profile change, record fresh evaluation
evidence outside the repository and review quality, safety, cost, and latency
deltas before release. Do not describe synthetic inputs as production
measurements. Keep environment-specific observations outside the repository
when they contain customer data, credentials, provider payloads, or proprietary
pricing.

A policy is releasable only when every reviewed candidate case reaches a completed terminal result, meets its task quality floor, produces no unsupported claims or unsafe actions, and does not increase per-task cost or latency without at least the configured justified quality gain.

## Identity

`Identity:Mode=DevelopmentHeaders` is accepted only when the host environment is exactly `Development`; the API fails startup closed in every other environment. Internet-facing deployments must use `Jwt`, configure an HTTPS authority and audience, and issue tokens containing the configured tenant, actor, and permission claims. The API replaces caller-supplied request scopes with authenticated scopes before policy evaluation. External-action approval requires both the action scope (for example `email:send`) and `approval:grant`.

## External-action safety and recovery

An external write requires explicit request intent, a tenant-scoped idempotency key, an allowed task capability, its configured permission scopes, and an approved durable action record. Events persist the policy version, actor, decision, and input/result references; they never intentionally persist payload bodies.

A completed external action is replayed from its durable result. A conflicting reference is rejected. If the process loses certainty after reserving or starting an action, the reservation remains `Running` and the runtime returns `external_action_in_progress` rather than invoking it again. iRoute cannot determine what happened outside itself, so only an operator can release that reservation.

### Reconciling an indeterminate action

`GET /v1/executions/{executionId}/external-actions` lists the reservations for an execution whose outcome is unknown. Both endpoints are tenant-scoped and require the `approval:grant` scope, because recording an outcome asserts whether an irreversible side effect happened.

Establish what actually occurred with the provider, then record it:

```bash
curl --request POST \
  "http://localhost:8080/v1/executions/$EXECUTION_ID/external-actions/send-email/reconcile" \
  --header 'Content-Type: application/json' \
  --header "X-Tenant-Id: $TENANT" \
  --header "X-Actor-Id: $OPERATOR" \
  --header 'X-Permission-Scopes: approval:grant' \
  --data '{"outcome":"succeeded","detail":"Provider message id 01H... confirms one delivery."}'
```

Recording `succeeded` stores the operator decision as the action result, so resubmitting the request with the same idempotency key reuses it instead of firing the action again. Recording `failed` releases the reservation so a resubmission may attempt the action once more. Either decision appends an `external_action.reconciled` event naming the deciding actor. Reconciliation is deliberately manual: automatic distributed reconciliation would have to guess, and guessing wrong sends a message twice.

## Scaling

The PostgreSQL API is horizontally scalable for durable submissions, persisted result/event reads, observability queries, and dashboard traffic. The Kubernetes reference starts two API replicas, spreads them across nodes when possible, rolls with zero unavailable replicas, and exposes an HPA range of two to ten pods. All replicas use the same external PostgreSQL schema and run with `Storage:AutoInitialize=false`.

Execution workers are independently scalable because each work item has one expiring lease owner and fencing token. The reference starts two rolling execution-worker replicas and one `Recreate` lifecycle-worker replica. Do not scale the lifecycle role: lifecycle sweeps do not yet own a distributed lease. Persisted cancellation is observed by the active execution worker heartbeat. External actions additionally use tenant-scoped idempotency reservations; uncertain provider outcomes still require operator reconciliation.

## Observability and telemetry

`GET /v1/observability/summary` returns tenant-scoped aggregates over a bounded time window and can filter by `taskType` and `policyVersion`. `GET /v1/observability/executions/{executionId}` returns an ordered timeline with its trace ID; a different tenant receives not found. The dashboard at `/dashboard/` calls only these authenticated data endpoints and displays execution summaries, redacted timelines, memory diagnostics, and W18 provider-route/circuit observations. Static assets are public, but no execution data is embedded in them.

A successful summary with zero observations means the dashboard is connected but no persisted execution matches the selected tenant, time range, task type, and policy. Confirm that the dashboard tenant matches the execution request before investigating storage or worker health. The connection indicator distinguishes this valid empty result from an authentication or API failure.

The observability `cost` field is the normalized value reported by the configured gateway. It has no currency field in the Core contract and must not be presented as provider-billed cost. All registered gateways in one deployment should report a consistent unit; reconcile financial reporting against provider billing separately.

The default limits are 90 query days, 1,000 sampled executions, 25 recent rows, 1,000 timeline events, and 1,000 characters per retained safe event string. A `truncated` flag identifies bounded results. Increase limits only after measuring database and response costs; the projection reads existing execution/event persistence and intentionally does not maintain a second analytics database.

`Observability:PayloadMode=MetadataOnly` is the default and replaces every event data object with a redaction marker, making unknown future event shapes fail closed. Operators may opt into `Redacted`, which removes input, output, content, value, prompt, response, request, body, raw, credential, secret, authorization, tenant/actor/project IDs, permission scopes, passwords, tokens, cookies, headers, and API-key variants recursively while bounding retained safe strings. Actor and project references are one-way SHA-256 prefixes. Never add payload, credential, scope values, raw identifiers, or unbounded strings to a trace tag, metric attribute, event, log, or dashboard field.

OpenTelemetry instruments ASP.NET Core, HTTP clients, runtime process metrics, and the custom `iRoute.Runtime` execution source/meter. Execution spans cover execute/resume, attach safe ordered event names, and record terminal quality, cost, tokens, latency, and call counts. Export remains disabled unless `OTEL_EXPORTER_OTLP_ENDPOINT` is configured. Correlate the span trace ID with the durable timeline when investigating a request; use task type and policy version—not tenant or actor—as comparison dimensions.

## Backup and recovery

Production requires PostgreSQL point-in-time recovery, object-store versioning when object storage is introduced, restoration drills, and documented recovery objectives. A backup is not accepted until a restore has been tested.

## Upgrade and privacy

Schema changes must use expand-and-contract migrations. API and worker versions must overlap during rolling upgrades. Retention, deletion, and export must remain tenant-scoped and eventually cover indexes, artifacts, memory, and evaluation samples.

## Container quick starts

### SQLite API and worker

```bash
docker compose -f deploy/compose.sqlite.yaml up --build --wait
curl --fail http://localhost:8080/health/live
curl --fail http://localhost:8080/health/ready
```

This is the smallest durable profile: non-root, read-only API and worker containers, one shared named SQLite volume, the deterministic gateway, and development identity headers. The worker processes queued executions and lifecycle sweeps. `docker compose ... down` preserves the named volume; use `down --volumes` only for an intentional reset.

### PostgreSQL with migration and worker

```bash
cp .env.example .env
docker compose -f deploy/compose.yaml up --build --wait
docker compose -f deploy/compose.yaml run --rm migrate status
```

The `migrate` service must complete successfully before either API or worker starts. The API and worker never race to initialize the production schema. Replace the default password and development identity mode before using this profile outside a trusted local environment.

## Schema migration commands

The migration executable reads standard .NET configuration and supports status, forward upgrade, and explicit rollback:

```bash
dotnet run --project src/Hosts/iRoute.Migrations -- status
dotnet run --project src/Hosts/iRoute.Migrations -- up
dotnet run --project src/Hosts/iRoute.Migrations -- up 20260802010000_GatewayCircuitBreaker
dotnet run --project src/Hosts/iRoute.Migrations -- down 20260801010000_RoutingDecisionCheckpoint --confirm
```

Set `Storage__Provider` and `ConnectionStrings__iRoute` for the target database. `status` reports the provider, current migration, applied migrations, pending migrations, unknown applied migrations, and whether the binary and database are current. `up` refuses to move backward. `down` refuses to run without both an explicit target and `--confirm` because reverse migrations may destroy data.

## Production upgrade procedure

1. Build and publish immutable, scanned API, worker, and migration images from the same commit. Never deploy a floating `latest` tag.
2. Confirm PostgreSQL backup/PITR health and record a restore point. Test restoration according to the service recovery objective.
3. Run contract, regression, deployment, migration, and provider-specific persistence tests for the release.
4. Apply only expand-phase schema changes. Run `iRoute.Migrations status`, then `up`, as a one-shot job using the release migration image.
5. Require a successful migration job and a schema-current `/health/ready` before allowing new API pods into service.
6. Roll API replicas with `maxUnavailable: 0`; verify liveness, readiness, an execution, SSE replay, and observability before completing the rollout.
7. Roll execution workers only after API health is stable, preserving at least one available replica; then replace the singleton lifecycle worker and confirm a successful sweep.
8. Leave contract-phase column/table removal to a later release after all old binaries and rollback windows have expired.

## Rollback procedure

Application rollback is the default: stop the rollout and restore the previous API/worker images while leaving the additive schema in place. Expand-and-contract compatibility exists specifically so the previous application can overlap with the new schema.

Run a schema `down` only when all application traffic and workers are drained, the previous binary cannot operate with the additive schema, a tested backup/restore point exists, and the migration's reverse data loss has been reviewed. Execute the exact previous migration target with `--confirm`, verify `status`, then deploy the matching binaries. If a failed migration partially changed external systems or data semantics, restore the database instead of improvising a reverse migration.

## Kubernetes reference deployment

The reference manifests assume an external highly available PostgreSQL service, a cluster metrics server for HPA, an ingress/TLS layer supplied by the operator, and images already published to a registry.

1. Replace the image registry/tags and every `example.invalid` value in `deploy/kubernetes`.
2. Apply `namespace.yaml`, `configmap.yaml`, and `serviceaccounts.yaml`.
3. Create `iroute-secrets` from the platform secret manager. `secret.example.yaml` documents required keys but must never receive real values in Git; local `secret.yaml` is ignored.
4. Create a unique migration job with `kubectl create -f deploy/kubernetes/migrate-job.yaml`, then wait for its generated Job name to complete and inspect its status output.
5. Apply the workloads with `kubectl apply -k deploy/kubernetes` and wait for `deployment/iroute-api`, `deployment/iroute-execution-worker`, and `deployment/iroute-lifecycle-worker` rollouts.
6. Verify `/health/live`, `/health/ready`, authenticated execution, event replay, and the dashboard through the operator-managed Service/Ingress.

The API Deployment starts with two replicas and can scale to ten on measured CPU. Resource requests make HPA input meaningful; a PodDisruptionBudget keeps one API replica available; topology spreading reduces single-node concentration. The execution-worker Deployment starts with two replicas using distributed leases; the lifecycle worker remains fixed at one. The migration Job is never part of the steady-state Deployment and uses a generated name so every release has an auditable run.
