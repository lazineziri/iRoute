# Engineering workstream status

Status date: 31 July 2026

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

## M1 — Deterministic kernel: in progress

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

## Next: W07 — Context compiler
