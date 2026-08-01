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
            "execution.queued",
            "execution.lease_claimed",
            "execution.lease_renewed",
            "execution.lease_released",
            "execution.status_changed",
            "resolution.considered",
            "routing.decided",
            "routing.escalated",
            "plan.validated",
            "policy.evaluated",
            "capability.denied",
            "capability.started",
            "capability.completed",
            "capability.failed",
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
            "gateway.started",
            "gateway.streamed",
            "gateway.completed",
            "gateway.failed",
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
            "routing_no_eligible_capability",
            "routing_budget_exceeded",
            "workflow_step_failed",
            "workflow_step_timed_out",
            "validation_failed",
            "execution_timed_out",
            "execution_cancelled",
            "execution_failed",
            "external_write_not_allowed",
            "capability_not_allowed",
            "capability_not_registered",
            "capability_contract_mismatch",
            "capability_invocation_failed",
            "capability_result_invalid",
            "capability_output_limit_exceeded",
            "capability_deadline_exceeded",
            "permission_scope_denied",
            "approval_not_found",
            "approval_already_decided",
            "approval_denied",
            "external_action_idempotency_required",
            "external_action_idempotency_conflict",
            "external_action_in_progress",
            "external_action_failed",
            "model_budget_exhausted",
            "context_budget_exceeded",
            "cost_budget_exceeded",
            "model_call_budget_exceeded",
            "model_gateway_unavailable",
            "model_gateway_http_error",
            "model_gateway_invalid_response"
        ];

        Assert.True(ErrorCodes.All.SetEquals(expected));
    }

    [Fact]
    public void ResolutionDecisionCodeRegistryContainsTheFrozenV1Values()
    {
        string[] expected =
        [
            "exact_cache_hit",
            "exact_cache_miss",
            "permission_denied",
            "unsupported_task",
            "project_scope_required",
            "state_key_required",
            "state_hit",
            "state_unavailable",
            "artifact_reference_required",
            "artifact_hit",
            "artifact_unavailable",
            "handler_unavailable",
            "handler_declined",
            "handler_stale",
            "handler_accepted",
            "external_write_blocked",
            "validation_failed"
        ];

        Assert.True(ResolutionDecisionCodes.All.SetEquals(expected));
    }
}
