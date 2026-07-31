# Engineering workstream status

Status date: 1 August 2026

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

## M2 — Measured routing: in progress

### W08 — Routing and planning: complete

Deliverables:

- Direct-path selector that bypasses the planner for every single-capability task.
- Deterministic bounded planner that compiles multi-capability task definitions into typed DAGs and fails closed before checkpointing when depth, step, model-call, or tool-call limits cannot fit.
- Capability matcher that enforces task coverage, allow lists, health, mandatory quality, latency, token capacity, cost, and call budgets.
- Versioned model-profile registry populated from evaluation measurements for small and strong generation/summarization routes.
- Measured escalation policy that bypasses a lower-cost route only when it is ineligible and records the precise rejection reason.
- Durable `RoutingDecision` checkpoint, `routing.decided` and `routing.escalated` audit events, selected `profileId` on the generic gateway request, and routing metadata on generated outcomes.
- Versioned routing/model-profile schemas, OpenAPI and Node SDK contracts, examples, error/event taxonomy updates, operational guidance, and PostgreSQL evaluation coverage.

Acceptance evidence:

- Unit tests prove a simple task returns a direct route with zero planner invocations and zero planning calls.
- Routing tests prove the default quality floor chooses the cheaper small profile, while a higher mandatory floor rejects it and escalates to the strong profile using measured quality, cost, latency, availability, reliability, uncertainty, and score inputs.
- Planner tests prove a two-capability workflow produces a typed depth-two DAG inside model/tool budgets and fails with `routing_budget_exceeded` when the permitted depth is one.
- Orchestrator tests prove the selected profile reaches the provider-neutral model gateway and the durable outcome/events explain the route and escalation without exposing payloads.
- The SQLite restart suite preserves the routing decision beside the plan; PostgreSQL evaluation checks direct planner avoidance, strong-profile escalation, measured candidates, and both routing audit events.

## Next: W09 — Generic model-gateway integration
