namespace iRoute.Common;

public static class ErrorCodes
{
    public const string IdempotencyKeyConflict = "idempotency_key_conflict";
    public const string IdentityScopeConflict = "identity_scope_conflict";
    public const string InvalidTaskRequest = "invalid_task_request";
    public const string ExecutionAlreadyTerminal = "execution_already_terminal";
    public const string UnknownTaskType = "unknown_task_type";
    public const string InvalidExecutionPlan = "invalid_execution_plan";
    public const string RoutingNoEligibleCapability = "routing_no_eligible_capability";
    public const string RoutingBudgetExceeded = "routing_budget_exceeded";
    public const string WorkflowStepFailed = "workflow_step_failed";
    public const string WorkflowStepTimedOut = "workflow_step_timed_out";
    public const string ValidationFailed = "validation_failed";
    public const string ExecutionTimedOut = "execution_timed_out";
    public const string ExecutionCancelled = "execution_cancelled";
    public const string ExecutionFailed = "execution_failed";
    public const string ExternalWriteNotAllowed = "external_write_not_allowed";
    public const string CapabilityNotAllowed = "capability_not_allowed";
    public const string CapabilityNotRegistered = "capability_not_registered";
    public const string CapabilityContractMismatch = "capability_contract_mismatch";
    public const string CapabilityInvocationFailed = "capability_invocation_failed";
    public const string CapabilityResultInvalid = "capability_result_invalid";
    public const string CapabilityOutputLimitExceeded = "capability_output_limit_exceeded";
    public const string CapabilityDeadlineExceeded = "capability_deadline_exceeded";
    public const string PermissionScopeDenied = "permission_scope_denied";
    public const string ApprovalNotFound = "approval_not_found";
    public const string ApprovalAlreadyDecided = "approval_already_decided";
    public const string ApprovalDenied = "approval_denied";
    public const string ExternalActionIdempotencyRequired = "external_action_idempotency_required";
    public const string ExternalActionIdempotencyConflict = "external_action_idempotency_conflict";
    public const string ExternalActionInProgress = "external_action_in_progress";
    public const string ExternalActionFailed = "external_action_failed";
    public const string ModelBudgetExhausted = "model_budget_exhausted";
    public const string ContextBudgetExceeded = "context_budget_exceeded";
    public const string CostBudgetExceeded = "cost_budget_exceeded";
    public const string ModelCallBudgetExceeded = "model_call_budget_exceeded";
    public const string ModelGatewayUnavailable = "model_gateway_unavailable";
    public const string ModelGatewayHttpError = "model_gateway_http_error";
    public const string ModelGatewayInvalidResponse = "model_gateway_invalid_response";
    public const string ModelGatewayExhausted = "model_gateway_exhausted";

    public static IReadOnlySet<string> All { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        IdempotencyKeyConflict,
        IdentityScopeConflict,
        InvalidTaskRequest,
        ExecutionAlreadyTerminal,
        UnknownTaskType,
        InvalidExecutionPlan,
        RoutingNoEligibleCapability,
        RoutingBudgetExceeded,
        WorkflowStepFailed,
        WorkflowStepTimedOut,
        ValidationFailed,
        ExecutionTimedOut,
        ExecutionCancelled,
        ExecutionFailed,
        ExternalWriteNotAllowed,
        CapabilityNotAllowed,
        CapabilityNotRegistered,
        CapabilityContractMismatch,
        CapabilityInvocationFailed,
        CapabilityResultInvalid,
        CapabilityOutputLimitExceeded,
        CapabilityDeadlineExceeded,
        PermissionScopeDenied,
        ApprovalNotFound,
        ApprovalAlreadyDecided,
        ApprovalDenied,
        ExternalActionIdempotencyRequired,
        ExternalActionIdempotencyConflict,
        ExternalActionInProgress,
        ExternalActionFailed,
        ModelBudgetExhausted,
        ContextBudgetExceeded,
        CostBudgetExceeded,
        ModelCallBudgetExceeded,
        ModelGatewayUnavailable,
        ModelGatewayHttpError,
        ModelGatewayInvalidResponse,
        ModelGatewayExhausted
    };
}
