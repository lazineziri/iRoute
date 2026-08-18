# Engineering workstream status

Status date: 2 August 2026

## M0 — Specification: complete

### W01 — Product boundaries and architecture decisions: complete

- Architecture decisions, non-goals, provider boundary, deployment/trust boundaries, and version policy are documented.
- Module ownership and dependency direction are explicit and architecture-tested.
- Public Contracts are separated from Core, Runtime, Infrastructure, and host composition.

### W02 — Contracts and schemas: complete

Deliverables:

- OpenAPI 3.1 specification for the v1 REST and SSE API.
- JSON Schema 2020-12 contracts for task requests/definitions, capabilities, plans, task events, outcomes, artifacts, evidence, problems, gateway messages, execution snapshots, and evaluation fixtures.
- Versioned SSE framing and replay contract.
- Stable v1 error taxonomy shared by runtime constants, OpenAPI, JSON Schema, examples, and tests.
- Public compatibility rules and a checked-in v1 contract snapshot.

Acceptance evidence:

- Invalid plans are rejected before capability execution. Semantic validation covers duplicate steps, missing dependencies, self-dependencies, cycles, maximum depth, maximum steps, potential model/tool attempts, step timeouts, task-definition identity, and the direct-executor shape.
- Every published contract example and every evaluation fixture is validated with a full JSON Schema 2020-12 validator.
- Backward-compatibility tests protect v1 operations, fields, required sets, statuses, resolution levels, event types, error codes, and schema identifiers in CI.

## M1 — Deterministic kernel: complete

### W03 — Execution state machine: complete

Deliverables:

- Formal execution and step states with immutable terminal execution states.
- Dependency-aware DAG scheduler bounded by both the plan and runtime configuration.
- Durable workflow plans, requests, step attempts, outputs, failures, and timestamps in SQLite and PostgreSQL.
- Per-step timeout tokens and execution-wide cancellation propagation.
- Retry checkpoints and restart recovery that resets interrupted steps while preserving completed outputs.
- Ordered workflow/step events for checkpoint, resume, start, completion, retry, and failure.

Acceptance evidence:

- A SQLite process-restart test completes one dependency, leaves the next step `Running`, creates a fresh store and scheduler, then proves the completed step is not invoked again.
- Cancellation tests prove the running handler observes cancellation and dependent steps never execute.
- A queue-capacity-one test proves producers wait under load and step concurrency never exceeds the lower configured/plan bound.
- A step-timeout test proves the timed-out step and cancelled downstream checkpoint states are durable.

### W04 — Policy, permissions and approvals: complete

Deliverables:

- Versioned task policy evaluation with capability allow lists, side-effect classes, request intent, and authenticated permission scopes.
- Durable tenant-scoped approval records and workflow resumption for policy-gated external actions.
- Idempotent external-action reservations, completed-result reuse, conflict detection, and fail-closed indeterminate-action handling.
- Immutable audit events for policy decisions, capability denials, approval decisions, and external-action lifecycle transitions. Audit data uses hashes/references instead of request or result payloads.
- Development and JWT identity boundaries that derive permission scopes at the server and prevent request-body scope escalation.

Acceptance evidence:

- Tests prove an external write cannot execute without write intent, the task capability, the required permission scope, and an authorized approval.
- Approval denial reaches a terminal auditable state without invoking the external executor.
- Repeated approval and task submissions reuse the recorded execution/action result and do not duplicate the side effect.
- A SQLite process-restart test creates a pending approval, reconstructs all durable stores, resumes the approved workflow, and executes the action once.

### W05 — Artifact and memory store: complete

Deliverables:

- Tenant- and project-scoped artifact lineages with stable logical keys, deterministic versions, content hashes, and active/superseded/invalidated lifecycle states.
- Versioned project facts and decisions extracted from typed request state, with evidence references and durable SQLite/PostgreSQL persistence.
- Normalized dependency edges from artifacts and memory to evidence, source state, memory versions, and upstream artifacts.
- Targeted invalidation that stays within the tenant boundary and recursively invalidates downstream artifacts without scanning or regenerating unrelated work.
- Tenant scope enforced by direct artifact and memory store lookups, including the public artifact retrieval boundary.

Acceptance evidence:

- Store tests prove unchanged values deduplicate, changed values create deterministic successor versions, supersession pointers are preserved, and cross-tenant direct lookup returns no record.
- Dependency tests prove a changed decision invalidates its dependent artifact and recursively invalidates derived artifacts while leaving another tenant untouched.
- A SQLite restart test proves memory, artifacts, lifecycle metadata, and normalized dependency edges survive reconstruction; the same migration is exercised against PostgreSQL.
- An end-to-end execution test changes an active project decision, observes memory supersession and artifact invalidation events, creates artifact version 2, then reuses that artifact with `ExactArtifact` and zero model calls.

### W06 — No-model resolver: complete

Deliverables:

- Ordered no-model resolver chain for exact scoped results, typed project facts/decisions, explicit artifact references, and registered deterministic task handlers.
- Typed `project.decision.get` and `project.fact.get` tasks backed by the W05 memory store and guarded by the authenticated `project:read` permission scope.
- Exact-cache identity that includes tenant, project, task definition version, logical artifact key, and canonical input fingerprint.
- Explicit artifact lookup by ID or logical key with tenant/project/task/version/type/lifecycle/freshness checks.
- Extensible deterministic-handler port with capability allow-list, permission, freshness, evidence, and task-output validation gates.
- Structured `resolution.considered` decisions that report resolver, acceptance, stable reason code, human-readable reason, permission/freshness checks, check count, and resolution level without exposing payloads.
- Semantic result matching remains intentionally disabled until measured embedding quality and tenant-safe index isolation are implemented.

Acceptance evidence:

- An end-to-end test materializes a project decision, retrieves it through `project.decision.get` with `StructuredState`, and proves the model gateway call count does not increase.
- Permission tests prove all resolvers reject before state lookup when `project:read` is missing and the execution fails with `permission_scope_denied` without generation.
- Resolver tests cover stale state, wrong-tenant state, stale artifacts, wrong-project artifacts, exact-cache logical-key isolation, deterministic-handler permission/capability/freshness gates, and evidence propagation.
- The PostgreSQL evaluation exercises accepted and rejected resolver decisions, zero-generation decision retrieval, and explicit artifact retrieval.

### W07 — Context compiler: complete

Deliverables:

- Deterministic source ranking across current decisions/facts, active project memory, authoritative request sources, explicit artifact sections, preferences, recent events, summaries, and bounded raw history.
- Tenant/project-scoped active-memory retrieval for both in-memory and Entity Framework stores, with lifecycle and expiry filtering at the persistence boundary.
- Explicit `contextArtifacts` retrieval by tenant-scoped artifact ID plus requested top-level section projection; unrelated artifact fields never enter model context.
- Logical-key supersession filtering, canonical-content deduplication, relevance ordering, and a maximum of three eligible raw-history items.
- Model-input projection that strips raw context-source fields, plus token admission based on projected task input and the complete serialized context after every insertion, guaranteeing `estimatedTokens <= budgetTokens`.
- Context manifests with source rank, inclusion/exclusion reason, exact output JSON path, `fullHistoryIncluded`, and an output-path-to-`EvidenceReference` provenance map.
- Versioned JSON Schema, OpenAPI, Node SDK, examples, event payload, operational guidance, and PostgreSQL evaluation coverage.

Acceptance evidence:

- Unit tests prove current request decisions supersede stored versions, exact duplicate content is removed, raw history is capped, and every included entry has a non-empty source reference.
- Artifact tests prove only explicitly requested sections from fresh, same-project artifacts are included and cross-project artifacts are rejected.
- Budget tests prove projected task input plus serialized context stays within a constrained task budget, reports every candidate exclusion, and fails closed before generation when essential task input cannot fit.
- The SQLite restart test proves active project memory remains available to the compiler after runtime reconstruction.
- The PostgreSQL evaluation verifies bounded history, deduplication, artifact slicing, provenance completeness, token bounds, and the `context.compiled` event.

## M2 — Measured routing: complete

### W08 — Routing and planning: complete

Deliverables:

- Direct-path selector that bypasses the planner for every single-capability task.
- Deterministic bounded planner that compiles multi-capability task definitions into typed DAGs and fails closed before checkpointing when depth, step, model-call, or tool-call limits cannot fit.
- Capability matcher that enforces task coverage, allow lists, health, mandatory quality, latency, token capacity, cost, and call budgets.
- Versioned model-profile registry with explicit synthetic provenance for the built-in small and strong generation/summarization routes, plus validated metadata for measured profiles.
- Measured escalation policy that bypasses a lower-cost route only when it is ineligible and records the precise rejection reason.
- Durable `RoutingDecision` checkpoint, `routing.decided` and `routing.escalated` audit events, selected `profileId` on the generic gateway request, and routing metadata on generated outcomes.
- Versioned routing/model-profile schemas, OpenAPI and Node SDK contracts, examples, error/event taxonomy updates, operational guidance, and PostgreSQL evaluation coverage.

Acceptance evidence:

- Unit tests prove a simple task returns a direct route with zero planner invocations and zero planning calls.
- Routing tests prove the default quality floor chooses the cheaper small profile, while a higher mandatory floor rejects it and escalates to the strong profile using sourced quality, cost, latency, availability, reliability, uncertainty, and score inputs.
- Planner tests prove a two-capability workflow produces a typed depth-two DAG inside model/tool budgets and fails with `routing_budget_exceeded` when the permitted depth is one.
- Orchestrator tests prove the selected profile reaches the provider-neutral model gateway and the durable outcome/events explain the route and escalation without exposing payloads.
- The SQLite restart suite preserves the routing decision beside the plan; PostgreSQL evaluation checks direct planner avoidance, strong-profile escalation, sourced candidates, and both routing audit events.

### W09 — Generic model-gateway integration: complete

Deliverables:

- Provider-neutral request/result contract with capability, selected profile, bounded input/context, output-token limit, correlation ID, and effective deadline.
- Buffered JSON and bounded NDJSON streaming transports behind the same `IModelGateway` port, with monotonic sequence, event-count, line-size, and terminal-completion enforcement.
- End-to-end cancellation propagation through HTTP send and stream reads.
- Normalized usage, configured gateway identity, transport, finish reason, and runtime-observed latency on successful calls.
- Normalized health contract plus `/health/model-gateway`, intentionally separate from API readiness.
- Classified failures for invalid request, authentication, rate limit, timeout, unavailability, invalid response, cancellation, and internal errors, with safe `gateway.failed` reporting.
- Versioned request/result/stream/health/failure schemas and examples, gateway lifecycle event taxonomy, .NET and Node SDK health access, a standalone gateway contract checker, and external-gateway evaluation coverage.

Acceptance evidence:

- Conformance tests prove separately identified buffered and streaming external gateways satisfy the same provider-neutral contract and normalize to the same result semantics.
- Streaming tests prove incremental events are consumed without buffering the HTTP response, sequence/completion bounds fail closed, and payload deltas are represented only as aggregate audit counts.
- Cancellation tests prove caller cancellation reaches the external HTTP handler without being converted into a retryable gateway failure.
- Failure tests prove HTTP statuses map consistently to retryability and normalized failure kinds; orchestration emits `gateway.failed` without provider bodies.
- Orchestrator and PostgreSQL evaluation prove selected profile/deadline propagation, configured gateway identity, normalized usage and observed latency, health reporting, and buffered/streaming lifecycle events without provider-specific planning logic.

### W10 — Evaluation and regression harness: complete

Deliverables:

- Versioned live-fixture, golden-dataset, per-case evaluation-result, and routing-comparison report contracts.
- Golden replay dataset covering all seven built-in tasks across normal, edge, adversarial, stale-memory, unauthorized-action, and dependency-change scenarios.
- Task-specific evaluator registry for output structure, golden assertions, evidence precision/coverage, unsupported claims, terminal status, authorization, and external side effects.
- Versioned baseline/candidate cost, latency, token, and model/tool-call benchmark inputs, aggregated per completed task.
- Deterministic JSON and Markdown comparison reports with aggregate and per-task quality, safety, cost, latency, and no-model resolution metrics.
- CI regression gate bound to the routing/model-profile source fingerprint and checked report snapshots.

Acceptance evidence:

- The harness independently discovers built-in task types and proves all seven have a registered evaluator plus all six required fixture categories: 42 candidate cases and 84 policy observations.
- Schema tests validate the golden dataset, every generated case result, and the comparison report with JSON Schema 2020-12; compatibility tests protect the new v1 schema identifiers and root fields.
- The candidate policy passes every task quality floor with zero safety failures and zero unsafe actions; quality, evidence, unsupported-claim rate, tokens, calls, cost, and latency are reported per completed task.
- The checked comparison reports show the task-aware policy against the full-history single-strong reference and reject any per-task cost or latency increase without the configured justified quality gain.
- CI runs `npm run test:regression`; routing/model-profile changes invalidate the recorded source fingerprint, while dataset changes invalidate the report fingerprint and snapshot.

## M3 — Capability ecosystem: complete

### W11 — Capability connectors: complete

Deliverables:

- Versioned `CapabilityDefinition`, invocation, result, execution-metadata, and classified-failure contracts shared by .NET, JSON Schema, examples, and the Node SDK.
- One normalized executor that resolves exactly one registered connector and enforces capability version, side-effect class, authenticated permission scopes, write idempotency, deadline, confidence, projection, and output-size limits.
- Deterministic reference connectors for email read/draft/send, calendar read/find slots, tenant-scoped allow-listed database reads, registered OpenAPI operations, registered MCP tools, and typed agent-result ingestion.
- Raw transport data is projected inside each connector. Only the projected `Output` member—not connector metadata, transport instructions, credentials, raw email bodies, or provider payloads—is admitted to dependent model context.
- Read-only tool steps run through orchestration with `capability.started`, `capability.completed`, and `capability.failed` events. Approved writes reuse the same normalized executor behind the durable external-action reservation boundary.
- Agent results require a supported schema version, provenance, bounded freshness, and dependencies. Database, OpenAPI, and MCP adapters reject arbitrary query text, destinations, servers, and tools.

Acceptance evidence:

- Connector conformance tests exercise every W11 transport and trust profile through the same request/result envelope and verify projected metadata, normalized usage, evidence, output hashes, permission enforcement, idempotency, and output bounds.
- Adversarial tests reject raw SQL, arbitrary OpenAPI destinations, unknown MCP tools, stale agent results, side-effect mismatches, missing scopes, and write invocations without an idempotency reference.
- Orchestrator tests complete `calendar.find_slots` and `database.answer` without model calls and prove a connector-to-model workflow contains only projected email fields.
- Existing approval tests continue to prove writes cannot execute without explicit intent, scope, authorized approval, and a durable idempotency reservation.
- The live evaluation exercises calendar and database connectors through the REST/SSE boundary and checks normalized usage plus connector lifecycle metadata.

### W12 — Lifecycle cleanup: complete

Deliverables:

- Validated lifecycle policy with default artifact and memory TTLs, cold-state delays, per-lineage version limits, tenant storage quotas, archive quotas, batch bounds, and sweep cadence.
- Default TTL assignment at both in-memory and durable write boundaries while preserving caller-specified expiry.
- Dependency-safe candidate selection that combines inactivity, lineage overflow, and tenant overflow without archiving or deleting a resource that still has an active dependent.
- Two-phase archive-then-delete processing with tenant-scoped, content-hashed payload archives, durable SQLite/PostgreSQL persistence, bounded retention, and deduplication through a composite archive identity.
- TTL and explicit-deletion propagation through memory and artifact dependency graphs, followed by dependency-index cleanup and supersession-pointer repair.
- A real asynchronous lifecycle worker, shared API/worker policy configuration, and a Compose worker image/profile.

Acceptance evidence:

- Active-dependency tests prove an old memory version remains present when an active artifact still references it.
- A quota workload creates 20 artifact versions and 10 memory versions, then proves two-phase cleanup converges to the configured two versions per lineage with no dangling dependency state.
- TTL tests prove an expired memory record invalidates a derived artifact before archival; explicit-deletion tests prove recursive invalidation and index removal in memory and SQLite.
- A SQLite restart test proves archives survive reconstruction, source deletion occurs only on a later sweep, storage remains bounded, and the lifecycle migration is applied.
- Worker tests prove cleanup starts asynchronously and respects host cancellation; the full unit, architecture, contract, regression, and SDK verification suites remain green.

### W13 — Observability and dashboard: complete

Deliverables:

- OpenTelemetry execution spans correlated by trace ID across orchestration events, plus metrics for starts, completions, failures, duration, quality, cost, input/output tokens, memory hits, and model calls avoided.
- A durable observability projection over the existing execution snapshots and ordered event stream, with equivalent in-memory and EF/SQLite/PostgreSQL query paths and no duplicate telemetry database.
- Tenant-scoped, bounded summary and execution-timeline endpoints with task/policy filters, hashed actor/project references, deterministic trace correlation, and cross-tenant not-found behavior.
- Quality, cost, latency, token, completion, no-model, and tool/model-call comparison views grouped by task type and routing policy version.
- Memory-hit diagnostics by resolver and stable acceptance/rejection code.
- Fail-closed payload controls: default `MetadataOnly` suppresses every event field; opt-in `Redacted` removes sensitive fields and bounds retained safe strings. Runtime traces and metrics never contain prompts, payloads, credentials, raw identifiers, or permission values.
- A responsive, dependency-free dashboard at `/dashboard/`, versioned OpenAPI and JSON Schemas/examples, compatibility coverage, and .NET/Node client access.

Acceptance evidence:

- Telemetry tests capture one complete execution span, correlate its trace ID with the durable timeline, observe terminal metrics, and prove tenant, actor, project, scope, and payload values are absent.
- Projection tests compare quality, cost, latency, tokens, and reuse across task/policy groups; a SQLite reconstruction test proves summaries and timelines survive process restart.
- Privacy tests prove sensitive nested fields are redacted, safe long strings are bounded, metadata-only mode exposes no event fields, and timelines are ordered, bounded, and tenant scoped.
- Contract tests compile both observability schemas and validate published examples; live API verification executes a task, queries its summary/timeline, and serves the dashboard assets.

### W14 — SDKs and CLI: complete

Deliverables:

- Idiomatic .NET, Node.js, Python, Java, PHP, and Rust protocol clients with execution, lookup, cancellation, approval, artifact, health, observability, SSE, and typed API-error surfaces.
- One language-neutral Base64-backed request/response, end-of-stream SSE, and RFC 9457 error fixture consumed by all six SDK test runners.
- Injectable transports in every non-.NET SDK for deterministic conformance checks and application-specific HTTP stacks without importing runtime behavior.
- A packageable `iroute` .NET CLI covering the public client operations, with local defaults and environment/flag-based connection, identity, and permission configuration.
- Runnable reference examples for every SDK and the CLI, all targeting the credential-free deterministic local profile.
- Native CI jobs for the six supported toolchain baselines plus architecture tests that constrain SDK and CLI dependencies to public protocol layers.

Acceptance evidence:

- Every SDK passes the identical byte-level request headers/body, final-frame SSE, response, and typed-error fixtures; CI runs each fixture in its native toolchain.
- The .NET SDK depends only on Contracts, the CLI depends only on the .NET SDK, and cross-language source is checked for references to Core, Runtime, Infrastructure, or host implementations.
- SDK implementations contain protocol serialization, HTTP transport, streaming, and error mapping only; routing, planning, model selection, prompts, memory, quality scoring, provider choice, and retry policy remain server-side.
- The CLI and six reference programs connect to `http://localhost:8080` with local tenant/actor defaults; the deterministic development runtime needs no provider credentials.

### W15 — Packaging and operations: complete

Deliverables:

- One multi-stage, non-root Dockerfile with independently publishable API, lifecycle-worker, and schema-migration targets, immutable-version metadata, read-only runtime compatibility, and API readiness health checks.
- A persistent SQLite quick start plus a PostgreSQL Compose profile that runs one migration service before starting API and worker roles.
- Liveness isolated from dependencies and readiness strengthened to require a reachable, schema-current durable store.
- A dedicated migration executable with status, forward-only upgrade, targeted upgrade, unknown-schema detection, and explicit confirmed rollback over the shared SQLite/PostgreSQL migrations.
- Kubernetes reference manifests for external PostgreSQL, a generated one-shot migration Job, two API replicas, rolling updates, HPA, PodDisruptionBudget, topology spreading, bounded resources, probes, and non-root/read-only containers.
- Production upgrade, application rollback, exceptional schema rollback, backup, secret, health, and horizontal-scaling procedures.

Acceptance evidence:

- The container smoke runner builds the API and worker images, starts the SQLite profile with a named volume, waits for schema-aware readiness, executes `email.draft`, verifies success, and removes only its isolated test stack.
- Migration tests start from an empty SQLite database, apply every checked-in migration, reject an unconfirmed downgrade, roll back to an explicit target, reapply the latest migration, and prove readiness changes from unhealthy to healthy.
- Deployment tests parse both Compose profiles and every Kubernetes YAML document, enforce migration-before-workload ordering, and prove execution workers scale independently while the lifecycle worker remains one replica.
- The PostgreSQL Compose and Kubernetes profiles set `Storage:AutoInitialize=false`; production migrations run once through the release-matched migration image instead of racing between replicas.
- The operations guide documents immutable image rollout, expand-and-contract sequencing, backup/restore gates, schema-current readiness, zero-unavailable API rolling updates, application-first rollback, and guarded destructive schema rollback.

### W16 — Open-source release: complete

Deliverables:

- Apache-2.0 source licensing and NOTICE attribution propagated into .NET,
  Node.js, Python, Java, PHP, and Rust package metadata.
- A contributor guide with clean setup, architectural boundaries, change-type
  evidence, compatibility/versioning rules, pull-request expectations,
  contribution terms, and Conventional Commit guidance.
- A private-advisory security policy with supported versions, reporting content,
  response expectations, scope, coordinated disclosure, and safe-research rules.
- Governance, support, Code of Conduct, structured bug/feature forms, and a pull
  request template that separates security reports from public collaboration.
- An explicit compatibility promise across the v1 wire contract, SDKs, stored
  state/migrations, configuration, deployment, deprecation, and stable support
  windows.
- Canonical `0.1.0-alpha.1` release metadata, changelog, release notes, clean
  installation guide, maintainer release procedure, and contributor-oriented
  architecture entry points.
- A reproducible release builder for source, NuGet, .NET tool, and npm artifacts
  with a machine-readable manifest and SHA-256 checksum file.
- Tag-gated GitHub prerelease automation plus a manual dry-run path; package
  registry and container publication remain explicit maintainer actions rather
  than guessed external side effects.

Acceptance evidence:

- Release-readiness tests align every package, Docker, Kubernetes, changelog,
  and release-note version with `release.json`; parse community/workflow YAML;
  and reject tracked private DOCX, local secret, or generated-state inputs.
- CI rebuilds the checksummed release candidate from a clean checkout only after
  .NET, contract, deployment, regression, six native SDK, and live container
  jobs pass.
- The installation guide follows the same restore, strict build, test, Compose
  startup, health, and real `email.draft` path exercised by CI.
- The security policy defines the private GitHub advisory channel and identifies
  the supported alpha version without promising an unavailable SLA.
- Contribution, compatibility, semantic versioning, changelog, migration,
  deprecation, tag immutability, artifact integrity, and rollback rules are
  explicit and cross-linked from the root documentation.

### W17 — Durable asynchronous execution: complete

Deliverables:

- `POST /v1/executions` persists the execution and returns HTTP `202` with a stable execution ID; model and tool plans enter a durable queue while successful no-model resolution stays inline.
- SQLite and PostgreSQL work stores with atomic enqueue, one-owner claims, expiring leases, heartbeats, delivery attempts, and fencing tokens that reject stale completion or abandonment.
- A separately scalable execution-worker role that resumes persisted workflow checkpoints after shutdown, lease loss, or process crash; completed step outputs remain authoritative.
- Persisted cancellation polling, approval-to-queue resumption, and external-action idempotency that prevent duplicate side effects under concurrent workers.
- Ordered queue/lease lifecycle events and existing sequence-backed SSE replay using `after` or `Last-Event-ID` reconnect cursors.
- Classified retries bounded by step call budgets and the absolute queued-execution deadline, with per-step timeouts, exponential backoff, deterministic jitter, and bounded `Retry-After` support.
- A seventh provider-aware schema migration, API/worker local Compose profile, two-replica Kubernetes execution-worker Deployment, and a separate singleton lifecycle-worker Deployment.

Acceptance evidence:

- Strict Release build and 111 unit tests cover immediate queueing before gateway invocation, no-model queue bypass, running cancellation, approval requeue, concurrent single claim, stale-token fencing, lease expiry, crash takeover, checkpoint recovery, retry classification, jitter, timeouts, and `Retry-After`.
- The PostgreSQL integration test runs two independent claimers against a real server and proves exactly one lease, worker interruption, expired-lease takeover, stale-owner rejection, automatic checkpoint recovery, heartbeat cancellation, and terminal completion.
- The live container smoke starts separate SQLite API and worker containers, observes HTTP `202`, polls the persisted execution across the host boundary, verifies `Succeeded`, and removes its isolated stack.
- Contract and deployment tests validate the additive `Queued` status, queue/lease events, `202` response, retry metadata, migration ordering, shared local volume, two scalable execution workers, and one lifecycle worker.

Completion caveats:

- Sustained load, soak testing, and production failover remain future production-readiness gates. They are not reasons to reopen the completed W17 workstream.
- The ignored Azure adopter lab is useful external evidence, but it is not a release gate until a sanitized, reproducible harness exists in the repository.

### W18 — Provider resilience and circuit breaking: complete

Deliverables:

- A provider-neutral deployment registry for multiple generic gateway routes, with gateway, provider, deployment, region, residency, model-version, quality, cost, latency, capability, profile, priority, and enabled metadata.
- Per-deployment closed/open/half-open circuits in memory and durable SQLite/PostgreSQL stores, including failure thresholds, bounded exponential open intervals, Retry-After extension, one fenced half-open probe lease, lease takeover, and stale-probe rejection.
- Deterministic failure classification for timeout, throttling, transport, provider, malformed output, validation, policy, and permanent failures.
- One model-resilience retry owner: model workflow steps have one scheduler attempt while the gateway layer performs bounded cross-deployment fallback; tool-step retry behavior is unchanged.
- Static fallback policy enforcing task quality floor, cumulative estimated cost, remaining deadline, allowed regions, required residency, model-call attempts, selected capability, and selected profile.
- Stable non-retryable `model_gateway_exhausted` behavior with complete candidate and attempt evidence when no eligible deployment remains.
- Durable trace events and OpenTelemetry metrics exposing rejected candidates, breaker state, failure class, fallback reason, attempts, final deployment, and all required gateway/deployment dimensions without provider payloads.
- An eighth additive schema migration plus versioned JSON Schema, OpenAPI, event/error taxonomy, Node SDK types, configuration, and operations guidance.

Acceptance evidence:

- Resilience tests cover 429 with Retry-After, repeated 5xx failures, slow deployments, malformed outputs, validation failure, partial recovery, open-circuit bypass, half-open concurrency, stale-probe fencing, full exhaustion, and fallback constrained by region, residency, cost, deadline, and attempts.
- The SQLite reconstruction test proves circuit state persists; the opt-in PostgreSQL test creates independent store instances and proves exactly one replica receives the half-open probe permit.
- An end-to-end orchestration test fails the primary route, succeeds on the fallback, records both attempts and the final deployment, opens the primary circuit, and proves no workflow-layer model retry was scheduled.
- Observability tests group attempted and rejected candidates by gateway, provider, deployment, region, model version, failure class, and circuit state.
- Adaptive routing and online learning remain deliberately outside W18; deterministic resilience must remain measurable before learned policy is introduced.

### W19 — Tenant quotas and fairness: next

Completion requires:

1. Distributed per-tenant concurrency limits.
2. Request-rate and queue-depth limits.
3. Input-token, output-token, and cost budgets.
4. Token/cost reservation before execution and reconciliation after completion.
5. Fair scheduling so a busy tenant cannot monopolize workers.
6. Separate limits for deterministic, connector, and model work.
7. Atomic enforcement across API and worker replicas.
8. Standard HTTP `429` responses with bounded `Retry-After` guidance.
9. Quota decisions recorded in durable traces and low-cardinality metrics.
10. PostgreSQL integration tests proving fairness across multiple workers while
    an intentionally abusive tenant is active.

PostgreSQL is the baseline quota implementation so Redis does not become a
mandatory dependency. A Redis backend may be added later as an optional
high-throughput implementation after the semantics are proven equivalent.

## Extended backlog status

All W01-W18 workstreams are implemented. The result is a credible resilient alpha,
not a production declaration. Release progression is:

- `0.1.0-alpha.1`: public open-source alpha.
- W19 plus security, provider, and load validation: private beta.
- Real operational evidence and completed runbooks: production candidate.
- Sustained successful pilots: `1.0`.

After W19, the next measured work should cover real connectors, semantic memory,
and production deployment validation.
