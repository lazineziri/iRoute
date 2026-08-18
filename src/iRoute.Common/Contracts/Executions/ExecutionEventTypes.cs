namespace iRoute.Common;

public static class ExecutionEventTypes
{
    public const string Created = "execution.created";
    public const string Queued = "execution.queued";
    public const string LeaseClaimed = "execution.lease_claimed";
    public const string LeaseRenewed = "execution.lease_renewed";
    public const string LeaseReleased = "execution.lease_released";
    public const string StatusChanged = "execution.status_changed";
    public const string ResolutionConsidered = "resolution.considered";
    public const string RoutingDecided = "routing.decided";
    public const string RoutingEscalated = "routing.escalated";
    public const string PlanValidated = "plan.validated";
    public const string PolicyEvaluated = "policy.evaluated";
    public const string CapabilityDenied = "capability.denied";
    public const string CapabilityStarted = "capability.started";
    public const string CapabilityCompleted = "capability.completed";
    public const string CapabilityFailed = "capability.failed";
    public const string ApprovalRequired = "approval.required";
    public const string ApprovalDecided = "approval.decided";
    public const string WorkflowCheckpointed = "workflow.checkpointed";
    public const string WorkflowResumed = "workflow.resumed";
    public const string StepStarted = "step.started";
    public const string StepCompleted = "step.completed";
    public const string StepRetryScheduled = "step.retry_scheduled";
    public const string StepFailed = "step.failed";
    public const string ExternalActionStarted = "external_action.started";
    public const string ExternalActionCompleted = "external_action.completed";
    public const string ExternalActionReused = "external_action.reused";
    public const string ExternalActionFailed = "external_action.failed";
    public const string ExternalActionReconciled = "external_action.reconciled";
    public const string ContextCompiled = "context.compiled";
    public const string GatewayStarted = "gateway.started";
    public const string GatewayStreamed = "gateway.streamed";
    public const string GatewayCompleted = "gateway.completed";
    public const string GatewayFailed = "gateway.failed";
    public const string GatewayCandidateEvaluated = "gateway.candidate_evaluated";
    public const string GatewayAttempted = "gateway.attempted";
    public const string GatewayFallbackSelected = "gateway.fallback_selected";
    public const string GatewayCircuitChanged = "gateway.circuit_changed";
    public const string GatewayExhausted = "gateway.exhausted";
    public const string GatewayResilienceDecided = "gateway.resilience_decided";
    public const string ValidationCompleted = "validation.completed";
    public const string ArtifactMaterialized = "artifact.materialized";
    public const string ArtifactSuperseded = "artifact.superseded";
    public const string ArtifactInvalidated = "artifact.invalidated";
    public const string MemoryMaterialized = "memory.materialized";
    public const string MemorySuperseded = "memory.superseded";
    public const string MemoryInvalidated = "memory.invalidated";
    public const string CancellationRequested = "execution.cancellation_requested";
    public const string Completed = "execution.completed";
    public const string Failed = "execution.failed";

    public static IReadOnlySet<string> All { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        Created,
        Queued,
        LeaseClaimed,
        LeaseRenewed,
        LeaseReleased,
        StatusChanged,
        ResolutionConsidered,
        RoutingDecided,
        RoutingEscalated,
        PlanValidated,
        PolicyEvaluated,
        CapabilityDenied,
        CapabilityStarted,
        CapabilityCompleted,
        CapabilityFailed,
        ApprovalRequired,
        ApprovalDecided,
        WorkflowCheckpointed,
        WorkflowResumed,
        StepStarted,
        StepCompleted,
        StepRetryScheduled,
        StepFailed,
        ExternalActionStarted,
        ExternalActionCompleted,
        ExternalActionReused,
        ExternalActionFailed,
        ExternalActionReconciled,
        ContextCompiled,
        GatewayStarted,
        GatewayStreamed,
        GatewayCompleted,
        GatewayFailed,
        GatewayCandidateEvaluated,
        GatewayAttempted,
        GatewayFallbackSelected,
        GatewayCircuitChanged,
        GatewayExhausted,
        GatewayResilienceDecided,
        ValidationCompleted,
        ArtifactMaterialized,
        ArtifactSuperseded,
        ArtifactInvalidated,
        MemoryMaterialized,
        MemorySuperseded,
        MemoryInvalidated,
        CancellationRequested,
        Completed,
        Failed
    };
}
