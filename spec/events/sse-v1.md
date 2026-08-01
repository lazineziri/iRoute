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
| `execution.queued` | The validated plan was committed to the durable work queue. |
| `execution.lease_claimed` | A worker atomically claimed the execution with a fenced lease. |
| `execution.lease_renewed` | The active worker renewed its lease and polled distributed cancellation. |
| `execution.lease_released` | A worker completed or safely abandoned its owned lease. |
| `resolution.considered` | A no-model resolver was accepted or rejected with a safe reason and permission/freshness results. |
| `routing.decided` | The versioned routing policy selected a direct or workflow path from measured candidates. |
| `routing.escalated` | A lower-cost route was bypassed because it failed a mandatory eligibility constraint. |
| `plan.validated` | A typed plan passed graph and budget validation. |
| `policy.evaluated` | The versioned capability, side-effect, scope, and approval policy produced a decision. |
| `capability.denied` | Policy denied a capability before its executor could run. |
| `capability.started` | A normalized connector invocation began with a registered capability version and deadline. |
| `capability.completed` | A connector returned a projected result; data contains safe identity, trust, transport, usage, and output-reference metadata. |
| `capability.failed` | A connector failed with a normalized code, kind, retryability, and safe connector identity. |
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
| `gateway.started` | A bounded provider-neutral gateway call began with a capability, profile, and deadline. |
| `gateway.streamed` | A streaming call completed; data contains event/delta/character counts, never generated content. |
| `gateway.completed` | The model gateway returned normalized usage, observed latency, transport, finish reason, and configured gateway identity. |
| `gateway.failed` | A call failed with a normalized failure kind, retryability, status, gateway identity, and observed latency. |
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

## Resolution consideration data

Every `resolution.considered` event contains `resolver`, `accepted`, `code`, `reason`, `permissionChecked`, `freshnessChecked`, `checks`, and nullable `level`. The exact-cache, fact/decision, artifact, and deterministic-handler resolvers emit one decision each until a candidate is accepted. Reasons describe checks and misses without including state or output payloads. The standalone `resolution-consideration.schema.json` contract is authoritative for this event data shape.

## Context compilation data

Every `context.compiled` event contains `estimatedTokens`, `budgetTokens`, `projectedInputTokens`, `contextTokens`, `truncated`, `fullHistoryIncluded`, `entries`, `included`, and `provenance`. These are counts and policy results only; source values and artifact content remain in the bounded model request and never enter the event stream. The outcome's `ContextManifest` contains the detailed inclusion/exclusion decisions and provenance map.

## Routing decision data

Every `routing.decided` event contains the routing policy version, direct/workflow path, selected capability and model profile, quality floor, expected quality/cost/latency, uncertainty, score, planner invocation count, escalation result, and all measured candidates with eligibility reasons. `routing.escalated` repeats that payload only when a cheaper candidate was rejected. These events contain policy measurements and identifiers, never prompts or outputs. The outcome's `RoutingDecision` is the durable public explanation.

## Model gateway data

`gateway.started` records only the step, capability, selected profile, and effective deadline. For streaming transports, `gateway.streamed` reports aggregate stream counts without persisting deltas. `gateway.completed` uses camel-case normalized usage fields and records `gatewayId`, `transport`, and `finishReason`. `gateway.failed` records the normalized failure kind and never copies a provider response body, credential, prompt, context, or generated output into the event stream.

## Capability connector data

`capability.started` records the step, capability version, side-effect class, and deadline. `capability.completed` records connector identity, capability kind, trust level, transport, normalized tool usage, the mandatory `projected=true` assertion, and a SHA-256 output reference. `capability.failed` records only normalized failure fields. Connector inputs, projected outputs, raw transport responses, MCP instructions, agent scratch data, credentials, and authorization material are never event data.
