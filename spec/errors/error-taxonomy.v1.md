# iRoute v1 error taxonomy

Errors use a stable lowercase `snake_case` code. HTTP boundary failures use RFC 9457 problem details with the code in `code`. Failures after an execution record exists appear in `ExecutionSnapshot.error` and in the terminal event.

| Code | Surface | Retryable default | Meaning |
|---|---|---:|---|
| `idempotency_key_conflict` | HTTP 400 | no | Header and body idempotency keys disagree. |
| `identity_scope_conflict` | HTTP 403 | no | Request scope conflicts with authenticated identity. |
| `invalid_task_request` | HTTP 400 | no | The request violates the v1 task-request contract. |
| `execution_already_terminal` | HTTP 409 | no | A terminal execution cannot be cancelled. |
| `unknown_task_type` | execution | no | No active task definition exists. |
| `invalid_execution_plan` | execution | no | The plan failed structural, DAG, or budget validation. |
| `routing_no_eligible_capability` | execution | no | No measured capability profile satisfies the mandatory quality, safety, health, capacity, deadline, and cost constraints. |
| `routing_budget_exceeded` | execution | no | A required workflow cannot fit its depth, model-call, tool-call, or step limits. |
| `workflow_step_failed` | execution | no | A bounded step exhausted its allowed attempts. |
| `workflow_step_timed_out` | execution | yes | A step exceeded its declared timeout. |
| `validation_failed` | execution | no | The outcome failed task-specific validation. |
| `execution_timed_out` | execution | yes | The execution deadline elapsed. |
| `execution_cancelled` | execution | no | Cancellation was requested. |
| `execution_failed` | execution | no | An unclassified internal execution failure occurred. |
| `external_write_not_allowed` | execution | no | The task requires external-write permission. |
| `capability_not_allowed` | execution | no | The compiled capability is outside the task definition's allow list. |
| `permission_scope_denied` | execution / HTTP 403 | no | The authenticated actor lacks a required action or approval scope. |
| `approval_not_found` | HTTP 404 | no | No tenant-visible pending or decided approval matches the action. |
| `approval_already_decided` | HTTP 409 | no | A conflicting decision was submitted for an approval or the execution is no longer waiting. |
| `approval_denied` | execution | no | An authorized actor denied the proposed external action. |
| `external_action_idempotency_required` | execution | no | An external action did not include a tenant-scoped idempotency key. |
| `external_action_idempotency_conflict` | execution | no | An idempotency reference is bound to another action or input. |
| `external_action_in_progress` | execution | yes | A prior action reservation is indeterminate and requires reconciliation. |
| `external_action_failed` | execution | no | The external capability failed after its durable reservation was acquired. |
| `model_budget_exhausted` | execution | no | Generation is required but the model-call budget is zero. |
| `context_budget_exceeded` | execution | no | The projected task input cannot fit inside the task input-token budget. |
| `cost_budget_exceeded` | execution | no | Reported cost exceeded the request ceiling. |
| `model_call_budget_exceeded` | execution | no | Reported model calls exceeded the request ceiling. |
| `model_gateway_unavailable` | execution | yes | The configured gateway could not be reached. |
| `model_gateway_http_error` | execution | classified | The gateway returned a non-success HTTP status. |
| `model_gateway_invalid_response` | execution | no | The gateway returned empty or invalid JSON. |

`model_gateway_http_error` is retryable for HTTP 408, 429, and 5xx responses. New codes may be added within v1; consumers must preserve and surface unknown codes rather than converting them to success.
