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

## Next: W04 — Policy, permissions and approvals

W04 has early foundations—side-effect classes, external-write denial, tenant identity, and idempotent task submission—but is not complete. Approval records/resumption, capability allow lists, scoped permissions, external-action idempotency, and complete audit events remain.
