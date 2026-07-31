using System.Text.Json;
using System.Text.Json.Serialization;

namespace iRoute.Contracts;

public sealed record TaskRequest(
    string TaskType,
    JsonElement Input,
    string? ProjectId = null,
    string? IdempotencyKey = null,
    TaskConstraints? Constraints = null,
    IReadOnlyDictionary<string, string>? Metadata = null,
    string? TenantId = null,
    string? ActorId = null,
    IReadOnlyList<string>? PermissionScopes = null);

public sealed record TaskConstraints(
    int? MaxInputTokens = null,
    int? MaxOutputTokens = null,
    decimal? MaxCost = null,
    int? DeadlineMilliseconds = null,
    decimal? MinimumQuality = null,
    bool RequireEvidence = false,
    bool AllowExternalWrites = false,
    int? MaxModelCalls = null,
    int? MaxToolCalls = null,
    int? MaxParallelCalls = null,
    int? MaxTaskDepth = null);

public sealed record ExecutionPlan(
    string PlanId,
    int Version,
    string TaskType,
    int TaskVersion,
    IReadOnlyList<ExecutionPlanStep> Steps,
    ExecutionPlanBudget Budget);

public sealed record ExecutionPlanStep(
    string Id,
    ExecutionStepKind Kind,
    string Capability,
    IReadOnlyList<string> DependsOn,
    SideEffectClass SideEffectClass,
    int TimeoutMilliseconds,
    int MaxAttempts = 1,
    string? ProfileId = null);

public sealed record ExecutionPlanBudget(
    int MaxSteps,
    int MaxModelCalls,
    int MaxToolCalls,
    int MaxParallelCalls,
    int MaxTaskDepth,
    int DeadlineMilliseconds,
    int? MaxInputTokens = null,
    int? MaxOutputTokens = null,
    decimal? MaxCost = null);

public sealed record ExecutionAccepted(
    Guid ExecutionId,
    ExecutionStatus Status,
    DateTimeOffset CreatedAt,
    Uri StatusUrl,
    Uri EventsUrl);

public sealed record ExecutionSnapshot(
    Guid ExecutionId,
    string TaskType,
    ExecutionStatus Status,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    TaskOutcome? Outcome = null,
    Problem? Error = null,
    string TenantId = "local",
    string ActorId = "local",
    string? ProjectId = null,
    int? TaskDefinitionVersion = null,
    DateTimeOffset? CancellationRequestedAt = null);

public sealed record TaskOutcome(
    JsonElement Output,
    ResolutionLevel ResolutionLevel,
    decimal Confidence,
    IReadOnlyList<EvidenceReference> Evidence,
    UsageSummary Usage,
    IReadOnlyList<ArtifactReference> Artifacts,
    ValidationSummary? Validation = null,
    ContextManifest? Context = null,
    RoutingDecision? Routing = null);

public sealed record RoutingDecision(
    string PolicyVersion,
    RoutingPath Path,
    string Reason,
    string SelectedCapability,
    string? SelectedProfileId,
    ModelTier? SelectedModelTier,
    decimal QualityFloor,
    decimal ExpectedQuality,
    decimal ExpectedCost,
    int ExpectedLatencyMilliseconds,
    decimal Uncertainty,
    decimal Score,
    bool PlannerInvoked,
    int PlanningCalls,
    bool Escalated,
    string? EscalationReason,
    IReadOnlyList<RoutingCandidateEvaluation> Candidates);

public sealed record RoutingCandidateEvaluation(
    string Capability,
    string? ProfileId,
    ModelTier? ModelTier,
    bool Eligible,
    string Reason,
    decimal ExpectedQuality,
    decimal ExpectedCost,
    int ExpectedLatencyMilliseconds,
    decimal Uncertainty,
    decimal Reliability,
    decimal Availability,
    decimal Score);

public sealed record UsageSummary(
    int InputTokens = 0,
    int OutputTokens = 0,
    decimal Cost = 0,
    long DurationMilliseconds = 0,
    int ModelCalls = 0,
    int ToolCalls = 0);

public sealed record EvidenceReference(
    string Kind,
    string Reference,
    string? ContentHash = null,
    DateTimeOffset? ObservedAt = null);

public sealed record ArtifactReference(
    Guid ArtifactId,
    string ArtifactType,
    int Version,
    string ContentHash);

public sealed record DependencyReference(
    string Kind,
    string Reference,
    string? ContentHash = null);

public sealed record ArtifactSnapshot(
    ArtifactReference Artifact,
    string TenantId,
    string? ProjectId,
    string TaskType,
    int TaskDefinitionVersion,
    JsonElement Content,
    IReadOnlyList<EvidenceReference> Evidence,
    DateTimeOffset CreatedAt,
    DateTimeOffset? ExpiresAt = null,
    bool IsActive = true,
    string? LogicalKey = null,
    ArtifactLifecycleStatus LifecycleStatus = ArtifactLifecycleStatus.Active,
    Guid? SupersedesArtifactId = null,
    Guid? SupersededByArtifactId = null,
    IReadOnlyList<DependencyReference>? Dependencies = null,
    DateTimeOffset? InvalidatedAt = null,
    string? InvalidationReason = null);

public sealed record MemorySnapshot(
    Guid MemoryId,
    string TenantId,
    string? ProjectId,
    MemoryKind Kind,
    string Key,
    int Version,
    JsonElement Value,
    string ContentHash,
    MemoryLifecycleStatus LifecycleStatus,
    IReadOnlyList<EvidenceReference> Evidence,
    IReadOnlyList<DependencyReference> Dependencies,
    DateTimeOffset CreatedAt,
    DateTimeOffset? ExpiresAt = null,
    Guid? SupersedesMemoryId = null,
    Guid? SupersededByMemoryId = null,
    DateTimeOffset? InvalidatedAt = null,
    string? InvalidationReason = null);

public sealed record ValidationSummary(
    bool Passed,
    decimal Quality,
    IReadOnlyList<string> Checks,
    IReadOnlyList<string> Failures);

public sealed record ContextManifest(
    int EstimatedTokens,
    int BudgetTokens,
    int ProjectedInputTokens,
    int ContextTokens,
    bool Truncated,
    bool FullHistoryIncluded,
    IReadOnlyList<ContextManifestEntry> Entries,
    IReadOnlyDictionary<string, EvidenceReference> Provenance);

public sealed record ContextManifestEntry(
    string Kind,
    string Reference,
    bool Included,
    string Reason,
    int EstimatedTokens,
    string? ContentHash = null,
    int Rank = 0,
    string? OutputPath = null);

public sealed record ApprovalDecision(
    string ActionId,
    bool Approved,
    string? Reason = null);

public sealed record ApprovalSnapshot(
    Guid ExecutionId,
    string ActionId,
    ApprovalStatus Status,
    string Capability,
    SideEffectClass SideEffectClass,
    IReadOnlyList<string> RequiredPermissionScopes,
    string RequestedByActorId,
    string? DecidedByActorId,
    string InputReference,
    string IdempotencyReference,
    DateTimeOffset CreatedAt,
    DateTimeOffset? DecidedAt = null,
    string? Reason = null);

public sealed record ApprovalResult(
    ApprovalSnapshot Approval,
    ExecutionSnapshot Execution);

public sealed record Problem(
    string Code,
    string Title,
    string Detail,
    bool Retryable = false,
    IReadOnlyDictionary<string, string>? Metadata = null);

public sealed record ExecutionEvent(
    long Sequence,
    Guid ExecutionId,
    string Type,
    DateTimeOffset OccurredAt,
    JsonElement Data);

public sealed record ResolutionConsideration(
    string Resolver,
    bool Accepted,
    string Code,
    string Reason,
    bool PermissionChecked,
    bool FreshnessChecked,
    int Checks,
    ResolutionLevel? Level = null);

public static class ResolutionDecisionCodes
{
    public const string ExactCacheHit = "exact_cache_hit";
    public const string ExactCacheMiss = "exact_cache_miss";
    public const string PermissionDenied = "permission_denied";
    public const string UnsupportedTask = "unsupported_task";
    public const string ProjectScopeRequired = "project_scope_required";
    public const string StateKeyRequired = "state_key_required";
    public const string StateHit = "state_hit";
    public const string StateUnavailable = "state_unavailable";
    public const string ArtifactReferenceRequired = "artifact_reference_required";
    public const string ArtifactHit = "artifact_hit";
    public const string ArtifactUnavailable = "artifact_unavailable";
    public const string HandlerUnavailable = "handler_unavailable";
    public const string HandlerDeclined = "handler_declined";
    public const string HandlerStale = "handler_stale";
    public const string HandlerAccepted = "handler_accepted";
    public const string ExternalWriteBlocked = "external_write_blocked";
    public const string ValidationFailed = "validation_failed";

    public static IReadOnlySet<string> All { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        ExactCacheHit,
        ExactCacheMiss,
        PermissionDenied,
        UnsupportedTask,
        ProjectScopeRequired,
        StateKeyRequired,
        StateHit,
        StateUnavailable,
        ArtifactReferenceRequired,
        ArtifactHit,
        ArtifactUnavailable,
        HandlerUnavailable,
        HandlerDeclined,
        HandlerStale,
        HandlerAccepted,
        ExternalWriteBlocked,
        ValidationFailed
    };
}

[JsonConverter(typeof(JsonStringEnumConverter<ExecutionStatus>))]
public enum ExecutionStatus
{
    Accepted,
    Resolving,
    Planning,
    WaitingForApproval,
    Running,
    Validating,
    Materializing,
    Compensating,
    Succeeded,
    Failed,
    Cancelled,
    TimedOut
}

[JsonConverter(typeof(JsonStringEnumConverter<ResolutionLevel>))]
public enum ResolutionLevel
{
    ExactArtifact,
    StructuredState,
    SemanticMemory,
    DeterministicCapability,
    SmallModel,
    StrongModel,
    VerifiedOrHuman
}

[JsonConverter(typeof(JsonStringEnumConverter<ExecutionStepKind>))]
public enum ExecutionStepKind
{
    Deterministic,
    Model,
    Tool,
    Approval
}

[JsonConverter(typeof(JsonStringEnumConverter<RoutingPath>))]
public enum RoutingPath
{
    Direct,
    Workflow
}

[JsonConverter(typeof(JsonStringEnumConverter<ModelTier>))]
public enum ModelTier
{
    Small,
    Strong,
    Verifier
}

[JsonConverter(typeof(JsonStringEnumConverter<SideEffectClass>))]
public enum SideEffectClass
{
    None,
    ReadOnly,
    ReversibleWrite,
    IrreversibleWrite
}

[JsonConverter(typeof(JsonStringEnumConverter<ApprovalStatus>))]
public enum ApprovalStatus
{
    Pending,
    Approved,
    Denied
}

[JsonConverter(typeof(JsonStringEnumConverter<ArtifactLifecycleStatus>))]
public enum ArtifactLifecycleStatus
{
    Active,
    Superseded,
    Invalidated
}

[JsonConverter(typeof(JsonStringEnumConverter<MemoryKind>))]
public enum MemoryKind
{
    Fact,
    Decision
}

[JsonConverter(typeof(JsonStringEnumConverter<MemoryLifecycleStatus>))]
public enum MemoryLifecycleStatus
{
    Active,
    Superseded,
    Invalidated
}

public static class ExecutionEventTypes
{
    public const string Created = "execution.created";
    public const string StatusChanged = "execution.status_changed";
    public const string ResolutionConsidered = "resolution.considered";
    public const string RoutingDecided = "routing.decided";
    public const string RoutingEscalated = "routing.escalated";
    public const string PlanValidated = "plan.validated";
    public const string PolicyEvaluated = "policy.evaluated";
    public const string CapabilityDenied = "capability.denied";
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
    public const string ContextCompiled = "context.compiled";
    public const string GatewayCompleted = "gateway.completed";
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
        StatusChanged,
        ResolutionConsidered,
        RoutingDecided,
        RoutingEscalated,
        PlanValidated,
        PolicyEvaluated,
        CapabilityDenied,
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
        ContextCompiled,
        GatewayCompleted,
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
        ModelGatewayInvalidResponse
    };
}
