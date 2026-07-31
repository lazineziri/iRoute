using iRoute.Contracts;

namespace iRoute.UnitTests;

public sealed class PublicContractTaxonomyTests
{
    [Fact]
    public void EventTypeRegistryContainsTheFrozenV1Values()
    {
        string[] expected =
        [
            "execution.created",
            "execution.status_changed",
            "resolution.considered",
            "plan.validated",
            "policy.evaluated",
            "capability.denied",
            "approval.required",
            "approval.decided",
            "workflow.checkpointed",
            "workflow.resumed",
            "step.started",
            "step.completed",
            "step.retry_scheduled",
            "step.failed",
            "external_action.started",
            "external_action.completed",
            "external_action.reused",
            "external_action.failed",
            "context.compiled",
            "gateway.completed",
            "validation.completed",
            "artifact.materialized",
            "artifact.superseded",
            "artifact.invalidated",
            "memory.materialized",
            "memory.superseded",
            "memory.invalidated",
            "execution.cancellation_requested",
            "execution.completed",
            "execution.failed"
        ];

        Assert.True(ExecutionEventTypes.All.SetEquals(expected));
    }

    [Fact]
    public void ErrorCodeRegistryContainsTheFrozenV1Values()
    {
        string[] expected =
        [
            "idempotency_key_conflict",
            "identity_scope_conflict",
            "invalid_task_request",
            "execution_already_terminal",
            "unknown_task_type",
            "invalid_execution_plan",
            "workflow_step_failed",
            "workflow_step_timed_out",
            "validation_failed",
            "execution_timed_out",
            "execution_cancelled",
            "execution_failed",
            "external_write_not_allowed",
            "capability_not_allowed",
            "permission_scope_denied",
            "approval_not_found",
            "approval_already_decided",
            "approval_denied",
            "external_action_idempotency_required",
            "external_action_idempotency_conflict",
            "external_action_in_progress",
            "external_action_failed",
            "model_budget_exhausted",
            "cost_budget_exceeded",
            "model_call_budget_exceeded",
            "model_gateway_unavailable",
            "model_gateway_http_error",
            "model_gateway_invalid_response"
        ];

        Assert.True(ErrorCodes.All.SetEquals(expected));
    }
}
