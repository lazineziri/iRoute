# iRoute v1 event-stream contract

`GET /v1/executions/{executionId}/events` returns `text/event-stream`. Each frame has exactly these fields:

```text
id: 4
event: plan.validated
data: {"sequence":4,"executionId":"...","type":"plan.validated","occurredAt":"...","data":{...}}
```

- `id` is the decimal `TaskEvent.sequence` and is strictly increasing within one execution.
- `event` is the `TaskEvent.type` value.
- `data` is one complete JSON value conforming to `task-event.schema.json`.
- A client reconnects with `Last-Event-ID` or `?after=`. The query parameter wins when both are supplied.
- Replay returns only events whose sequence is greater than the cursor.
- The server closes a replay stream after the execution is terminal and no newer event remains.
- Consumers must ignore unknown event types and unknown fields so v1 can add events without breaking them.
- Producers must not place secrets, prompts, generated payloads, or artifact bodies in event data. Events contain identifiers, counts, hashes, states, and policy decisions.

## Event types

| Type | Meaning |
|---|---|
| `execution.created` | The durable execution record exists. |
| `execution.status_changed` | The state machine accepted a transition. |
| `resolution.considered` | A no-model resolver candidate was accepted or rejected. |
| `plan.validated` | A typed plan passed graph and budget validation. |
| `policy.evaluated` | The versioned capability, side-effect, scope, and approval policy produced a decision. |
| `capability.denied` | Policy denied a capability before its executor could run. |
| `approval.required` | A durable pending approval was created for an external action. |
| `approval.decided` | An authorized actor durably approved or denied the action. |
| `workflow.checkpointed` | The validated plan and initial step states were persisted. |
| `workflow.resumed` | An existing checkpoint was loaded after interruption. |
| `step.started` | A durable step attempt began. |
| `step.completed` | A step output was checkpointed successfully. |
| `step.retry_scheduled` | A failed attempt was reset within its retry bound. |
| `step.failed` | A step failed, timed out, or was cancelled. |
| `external_action.started` | An idempotency reservation was acquired and the external executor was invoked. |
| `external_action.completed` | The external action and its result reference were durably recorded. |
| `external_action.reused` | A completed idempotent action result was reused without invoking the executor. |
| `external_action.failed` | The action failed or became indeterminate; event data contains references, not payloads. |
| `context.compiled` | The bounded context manifest was created. |
| `gateway.completed` | The model gateway returned normalized usage. |
| `validation.completed` | Task-specific validation completed. |
| `artifact.materialized` | A versioned artifact was stored. |
| `artifact.superseded` | A new artifact version records the artifact version it supersedes. |
| `artifact.invalidated` | One or more artifacts became stale because a dependency changed or disappeared. |
| `memory.materialized` | A scoped fact or decision version was stored. |
| `memory.superseded` | A fact or decision was replaced by a newer scoped version. |
| `memory.invalidated` | Derived memory became stale because a dependency changed or disappeared. |
| `execution.cancellation_requested` | Cancellation was durably requested. |
| `execution.completed` | The execution succeeded. |
| `execution.failed` | The execution failed, was cancelled, or timed out. |

The JSON Schema and OpenAPI component are authoritative when this prose and a machine-readable contract differ.
