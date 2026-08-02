using System.Runtime.CompilerServices;
using System.Text.Json;
using iRoute.Contracts;

namespace iRoute.Core;

/// <summary>
/// An execution recorded against an idempotency key, with the fingerprint of the request that
/// created it so a replay carrying a different payload can be told apart from a genuine retry.
/// </summary>
public sealed record ExecutionSubmission(ExecutionSnapshot Execution, string? InputFingerprint);

/// <summary>
/// Thrown when an idempotency key is already recorded for the tenant. Concurrent submits with the
/// same key race, so callers must treat this as "someone else won" and re-read, not as an error.
/// </summary>
public sealed class IdempotencyConflictException(string tenantId, string idempotencyKey)
    : Exception($"Idempotency key '{idempotencyKey}' already exists for tenant '{tenantId}'.")
{
    public string TenantId { get; } = tenantId;
    public string IdempotencyKey { get; } = idempotencyKey;
}

/// <summary>
/// Thrown when an idempotency key is replayed with a different request payload. Unlike
/// <see cref="IdempotencyConflictException"/> this is a client error, not a race: answering with
/// the original execution would hide the mistake, so it is reported instead.
/// </summary>
public sealed class IdempotencyKeyReusedException(string idempotencyKey)
    : Exception($"Idempotency key '{idempotencyKey}' was already used for a different request payload.")
{
    public string IdempotencyKey { get; } = idempotencyKey;
}

public interface IExecutionStore
{
    Task<ExecutionSubmission?> FindByIdempotencyKeyAsync(
        string tenantId,
        string key,
        CancellationToken cancellationToken);
    Task<ExecutionSnapshot?> GetAsync(Guid executionId, CancellationToken cancellationToken);

    /// <summary>
    /// Inserts a new execution.
    /// </summary>
    /// <exception cref="IdempotencyConflictException">
    /// The tenant already has an execution for <paramref name="idempotencyKey"/>. Raised instead of
    /// a provider-specific unique-violation so concurrent submits can be resolved by re-reading.
    /// </exception>
    Task CreateAsync(
        ExecutionSnapshot execution,
        string? idempotencyKey,
        string? inputFingerprint,
        CancellationToken cancellationToken);
    /// <summary>
    /// Persists the mutable execution state. <see cref="ExecutionSnapshot.CancellationRequestedAt"/>
    /// is owned by the store and is never written from the supplied snapshot, so a transition
    /// computed before a cancellation arrived cannot erase it.
    /// </summary>
    Task UpdateAsync(ExecutionSnapshot execution, CancellationToken cancellationToken);

    /// <summary>
    /// Records a cancellation request without touching any other column.
    /// </summary>
    /// <returns>
    /// <see langword="false"/> when the execution is missing or already terminal, in which case
    /// nothing is written and the recorded outcome is preserved.
    /// </returns>
    Task<bool> TryRequestCancellationAsync(
        Guid executionId,
        DateTimeOffset requestedAt,
        CancellationToken cancellationToken);

    IAsyncEnumerable<ExecutionEvent> ReadEventsAsync(Guid executionId, long afterSequence, CancellationToken cancellationToken);
    Task<ExecutionEvent> AppendEventAsync(
        Guid executionId,
        string eventType,
        DateTimeOffset occurredAt,
        JsonElement data,
        CancellationToken cancellationToken);
}

public interface IExecutionWorkStore
{
    Task<ExecutionWorkItem> EnqueueAsync(
        Guid executionId,
        ExecutionStatus expectedStatus,
        DateTimeOffset queuedAt,
        CancellationToken cancellationToken);

    Task<ExecutionLease?> TryClaimAsync(
        string workerId,
        DateTimeOffset claimedAt,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken);

    Task<ExecutionLeaseHeartbeat> RenewAsync(
        ExecutionLease lease,
        DateTimeOffset renewedAt,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken);

    Task<bool> CompleteAsync(
        ExecutionLease lease,
        DateTimeOffset completedAt,
        CancellationToken cancellationToken);

    Task<bool> AbandonAsync(
        ExecutionLease lease,
        DateTimeOffset availableAt,
        CancellationToken cancellationToken);

    Task<bool> CancelPendingAsync(
        Guid executionId,
        DateTimeOffset cancelledAt,
        Problem problem,
        CancellationToken cancellationToken);

    Task<ExecutionWorkItem?> GetAsync(Guid executionId, CancellationToken cancellationToken);
}

public interface IArtifactStore
{
    Task<ArtifactRecord?> FindReusableAsync(
        ArtifactReuseQuery query,
        CancellationToken cancellationToken);
    Task<ArtifactRecord?> GetAsync(
        string tenantId,
        Guid artifactId,
        CancellationToken cancellationToken);
    Task<ArtifactRecord?> FindActiveAsync(
        ArtifactLookupQuery query,
        CancellationToken cancellationToken);
    Task<ArtifactRecord> SaveAsync(ArtifactRecord artifact, CancellationToken cancellationToken);
    Task<ArtifactInvalidationResult> InvalidateByDependencyAsync(
        DependencyChange change,
        CancellationToken cancellationToken);
}

public interface INoModelResolver
{
    string Name { get; }
    int Order { get; }
    Task<ResolutionDecision> ResolveAsync(
        TaskRequest request,
        TaskDefinition definition,
        CancellationToken cancellationToken);
}

public interface IDeterministicTaskHandler
{
    string Name { get; }
    string Capability { get; }
    bool Supports(TaskDefinition definition);
    Task<DeterministicHandlerResult?> TryResolveAsync(
        TaskRequest request,
        TaskDefinition definition,
        CancellationToken cancellationToken);
}

public interface IModelGateway
{
    string GatewayId => "unspecified";

    Task<ModelGatewayResult> ExecuteAsync(ModelGatewayRequest request, CancellationToken cancellationToken);

    async IAsyncEnumerable<ModelGatewayStreamEvent> StreamAsync(
        ModelGatewayRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var result = await ExecuteAsync(request, cancellationToken);
        yield return new ModelGatewayStreamEvent(
            1,
            ModelGatewayStreamEventKind.Completed,
            Result: result);
    }

    Task<ModelGatewayHealth> CheckHealthAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(new ModelGatewayHealth(
            GatewayId,
            ModelGatewayHealthStatus.Degraded,
            0,
            DateTimeOffset.UtcNow,
            "The gateway does not expose a health probe."));
    }
}

public sealed class ModelGatewayException(
    string code,
    string message,
    bool retryable,
    int? statusCode = null,
    Exception? innerException = null,
    ModelGatewayFailureKind failureKind = ModelGatewayFailureKind.Internal,
    string? gatewayId = null,
    string? correlationId = null,
    TimeSpan? retryAfter = null,
    GatewayFailureClass? failureClass = null,
    GatewayResilienceTrace? resilience = null) : Exception(message, innerException)
{
    public string Code { get; } = code;
    public bool Retryable { get; } = retryable;
    public int? StatusCode { get; } = statusCode;
    public ModelGatewayFailureKind FailureKind { get; } = failureKind;
    public string? GatewayId { get; } = gatewayId;
    public string? CorrelationId { get; } = correlationId;
    public TimeSpan? RetryAfter { get; } = retryAfter;
    public GatewayFailureClass? FailureClass { get; } = failureClass;
    public GatewayResilienceTrace? Resilience { get; } = resilience;

    public ModelGatewayFailure ToFailure() => new(
        Code,
        FailureKind,
        Message,
        Retryable,
        StatusCode,
        GatewayId,
        CorrelationId,
        RetryAfter is { } retryAfterValue
            ? checked((int)Math.Clamp(retryAfterValue.TotalMilliseconds, 0, int.MaxValue))
            : null,
        FailureClass,
        Resilience);
}

public interface IGatewayDeploymentRegistry
{
    Task<IReadOnlyList<GatewayDeployment>> ListAsync(CancellationToken cancellationToken);
}

public interface IGatewayDeploymentClientFactory
{
    IModelGateway GetClient(GatewayDeployment deployment);
}

public interface IGatewayCircuitStore
{
    Task<GatewayCircuitPermit> TryAcquireAsync(
        string deploymentId,
        string ownerId,
        GatewayCircuitPolicy policy,
        DateTimeOffset now,
        CancellationToken cancellationToken);

    Task<GatewayCircuitSnapshot> RecordSuccessAsync(
        GatewayCircuitPermit permit,
        DateTimeOffset now,
        CancellationToken cancellationToken);

    Task<GatewayCircuitSnapshot> RecordFailureAsync(
        GatewayCircuitPermit permit,
        GatewayFailureClass failureClass,
        bool countsTowardCircuit,
        TimeSpan? retryAfter,
        GatewayCircuitPolicy policy,
        DateTimeOffset now,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<GatewayCircuitSnapshot>> ListAsync(CancellationToken cancellationToken);
}

public sealed record GatewayDeployment(
    string RouteId,
    string GatewayId,
    string Provider,
    string DeploymentId,
    string Region,
    string Residency,
    string ModelVersion,
    IReadOnlyList<string> Capabilities,
    IReadOnlyList<string> ProfileIds,
    decimal ExpectedQuality,
    decimal EstimatedCost,
    int ExpectedLatencyMilliseconds,
    int Priority = 100,
    bool Enabled = true)
{
    public GatewayDeploymentReference ToReference() => new(
        GatewayId,
        Provider,
        DeploymentId,
        Region,
        Residency,
        ModelVersion);

    public bool Supports(string capability, string? profileId) =>
        (Capabilities.Contains("*", StringComparer.Ordinal) ||
         Capabilities.Contains(capability, StringComparer.Ordinal)) &&
        (ProfileIds.Contains("*", StringComparer.Ordinal) ||
         profileId is null ||
         ProfileIds.Contains(profileId, StringComparer.Ordinal));
}

public sealed record GatewayCircuitPolicy(
    int FailureThreshold = 3,
    TimeSpan? OpenDuration = null,
    TimeSpan? MaximumOpenDuration = null,
    TimeSpan? ProbeLeaseDuration = null)
{
    public TimeSpan EffectiveOpenDuration => OpenDuration ?? TimeSpan.FromSeconds(30);
    public TimeSpan EffectiveMaximumOpenDuration => MaximumOpenDuration ?? TimeSpan.FromMinutes(5);
    public TimeSpan EffectiveProbeLeaseDuration => ProbeLeaseDuration ?? TimeSpan.FromSeconds(15);

    public void EnsureValid()
    {
        if (FailureThreshold < 1 ||
            EffectiveOpenDuration <= TimeSpan.Zero ||
            EffectiveMaximumOpenDuration < EffectiveOpenDuration ||
            EffectiveProbeLeaseDuration <= TimeSpan.Zero)
        {
            throw new InvalidOperationException("Gateway circuit thresholds and durations are invalid.");
        }
    }
}

public sealed record GatewayCircuitSnapshot(
    string DeploymentId,
    GatewayCircuitState State,
    int ConsecutiveFailures,
    int OpenCount,
    DateTimeOffset? OpenedAt,
    DateTimeOffset? NextProbeAt,
    string? ProbeOwner,
    Guid? ProbeToken,
    DateTimeOffset? ProbeLeaseExpiresAt,
    GatewayFailureClass? LastFailureClass,
    DateTimeOffset? LastFailureAt,
    DateTimeOffset UpdatedAt);

public sealed record GatewayCircuitPermit(
    string DeploymentId,
    string OwnerId,
    bool Granted,
    GatewayCircuitState State,
    Guid? ProbeToken,
    string Reason,
    GatewayCircuitSnapshot Snapshot);

public interface IContextCompiler
{
    Task<CompiledContext> CompileAsync(
        TaskRequest request,
        TaskDefinition definition,
        CancellationToken cancellationToken);
}

public interface ITaskOutcomeValidator
{
    bool Supports(string taskType);
    Task<OutcomeValidationResult> ValidateAsync(
        TaskRequest request,
        TaskDefinition definition,
        ModelGatewayResult result,
        CompiledContext context,
        CancellationToken cancellationToken);
}

public interface IInputFingerprint
{
    string Create(TaskRequest request, int taskDefinitionVersion);

    /// <summary>
    /// Fingerprints a request as submitted, before any task definition has been resolved, so two
    /// submissions carrying the same idempotency key can be compared.
    /// </summary>
    string CreateForSubmission(TaskRequest request);
}

public interface IExecutionCancellationRegistry
{
    CancellationToken Register(Guid executionId, CancellationToken requestCancellation);
    bool RequestCancellation(Guid executionId);
    void Complete(Guid executionId);
}

public interface ITaskDefinitionRegistry
{
    Task<TaskDefinition?> FindAsync(string taskType, CancellationToken cancellationToken);
}

public interface ITaskRouter
{
    Task<RoutingResult> RouteAsync(
        TaskRequest request,
        TaskDefinition definition,
        CancellationToken cancellationToken);
}

public interface IDirectPathSelector
{
    Task<RoutingResult?> TrySelectAsync(
        TaskRequest request,
        TaskDefinition definition,
        CancellationToken cancellationToken);
}

public interface IBoundedTaskPlanner
{
    Task<RoutingResult> PlanAsync(
        TaskRequest request,
        TaskDefinition definition,
        CancellationToken cancellationToken);
}

public interface ICapabilityMatcher
{
    Task<CapabilityMatchResult> MatchAsync(
        TaskRequest request,
        TaskDefinition definition,
        string capability,
        CancellationToken cancellationToken);
}

public interface IModelProfileRegistry
{
    Task<IReadOnlyList<ModelProfile>> ListAsync(
        string capability,
        CancellationToken cancellationToken);
}

public interface IEscalationPolicy
{
    CapabilitySelection SelectCandidate(
        string capability,
        decimal qualityFloor,
        IReadOnlyList<CapabilityCandidate> candidates);
}

public interface IExecutionPlanValidator
{
    ExecutionPlanValidationResult Validate(ExecutionPlan plan);
    void EnsureValid(ExecutionPlan plan);
}

public interface IClock
{
    DateTimeOffset UtcNow { get; }
}

public enum ExecutionWorkState
{
    Pending,
    Leased,
    Completed,
    Cancelled
}

public sealed record ExecutionWorkItem(
    Guid ExecutionId,
    ExecutionWorkState State,
    DateTimeOffset AvailableAt,
    int DeliveryAttempt,
    string? LeaseOwner = null,
    Guid? LeaseToken = null,
    DateTimeOffset? LeaseExpiresAt = null,
    DateTimeOffset? HeartbeatAt = null,
    DateTimeOffset? CompletedAt = null);

public sealed record ExecutionLease(
    Guid ExecutionId,
    string WorkerId,
    Guid LeaseToken,
    int DeliveryAttempt,
    DateTimeOffset ExpiresAt);

public sealed record ExecutionLeaseHeartbeat(
    bool Renewed,
    bool CancellationRequested,
    DateTimeOffset? ExpiresAt = null);

public sealed record ResolutionCandidate(
    ResolutionLevel Level,
    JsonElement Output,
    decimal Confidence,
    IReadOnlyList<EvidenceReference> Evidence,
    ArtifactReference? Artifact = null,
    UsageSummary? Usage = null);

public sealed record ResolutionDecision(
    bool Accepted,
    string Code,
    string Reason,
    bool PermissionChecked,
    bool FreshnessChecked,
    IReadOnlyList<string> Checks,
    ResolutionCandidate? Candidate = null);

public sealed record DeterministicHandlerResult(
    JsonElement Output,
    decimal Confidence,
    IReadOnlyList<EvidenceReference> Evidence,
    DateTimeOffset? ExpiresAt = null,
    IReadOnlyList<string>? Checks = null);

public sealed record CompiledContext(
    JsonElement Content,
    ContextManifest Manifest,
    IReadOnlyList<EvidenceReference> Evidence,
    JsonElement ProjectedInput);

public sealed record OutcomeValidationResult(
    bool Passed,
    decimal Quality,
    IReadOnlyList<string> Checks,
    IReadOnlyList<string> Failures)
{
    public ValidationSummary ToContract() => new(Passed, Quality, Checks, Failures);
}

public sealed record ArtifactReuseQuery(
    string TenantId,
    string? ProjectId,
    string TaskType,
    int TaskDefinitionVersion,
    string InputHash,
    string LogicalKey,
    DateTimeOffset At);

public sealed record ArtifactLookupQuery(
    string TenantId,
    string? ProjectId,
    string TaskType,
    int TaskDefinitionVersion,
    string ArtifactType,
    string LogicalKey,
    DateTimeOffset At);

public sealed record ArtifactRecord(
    Guid ArtifactId,
    string TenantId,
    string? ProjectId,
    string TaskType,
    int TaskDefinitionVersion,
    string ArtifactType,
    int Version,
    string InputHash,
    string ContentHash,
    JsonElement Content,
    IReadOnlyList<EvidenceReference> Evidence,
    DateTimeOffset CreatedAt,
    DateTimeOffset? ExpiresAt,
    bool IsActive,
    string? LogicalKey = null,
    ArtifactLifecycleStatus LifecycleStatus = ArtifactLifecycleStatus.Active,
    Guid? SupersedesArtifactId = null,
    Guid? SupersededByArtifactId = null,
    IReadOnlyList<DependencyReference>? Dependencies = null,
    DateTimeOffset? InvalidatedAt = null,
    string? InvalidationReason = null)
{
    public string EffectiveLogicalKey =>
        string.IsNullOrWhiteSpace(LogicalKey) ? TaskType : LogicalKey.Trim();

    public IReadOnlyList<DependencyReference> EffectiveDependencies => Dependencies ?? [];

    public ArtifactReference ToReference() => new(ArtifactId, ArtifactType, Version, ContentHash);

    public ArtifactSnapshot ToSnapshot() => new(
        ToReference(),
        TenantId,
        ProjectId,
        TaskType,
        TaskDefinitionVersion,
        Content,
        Evidence,
        CreatedAt,
        ExpiresAt,
        IsActive,
        EffectiveLogicalKey,
        LifecycleStatus,
        SupersedesArtifactId,
        SupersededByArtifactId,
        EffectiveDependencies,
        InvalidatedAt,
        InvalidationReason);
}

public sealed record TaskDefinition(
    string TaskType,
    int Version,
    string Capability,
    int DefaultMaxOutputTokens,
    decimal MinimumQuality,
    bool RequiresEvidence,
    SideEffectClass SideEffectClass,
    string ArtifactType,
    int DefaultMaxInputTokens = 4000,
    int DefaultDeadlineMilliseconds = 30000,
    int DefaultMaxModelCalls = 1,
    TimeSpan? ArtifactTimeToLive = null,
    IReadOnlyList<string>? AllowedCapabilities = null,
    IReadOnlyList<string>? PermissionScopes = null,
    bool ApprovalRequired = false,
    IReadOnlyList<string>? RequiredCapabilities = null,
    int DefaultMaxToolCalls = 0,
    int DefaultMaxParallelCalls = 1,
    int DefaultMaxTaskDepth = 1,
    decimal? DefaultMaxCost = null,
    RoutingWeights? RoutingWeights = null,
    int DefaultMaxAttempts = 3)
{
    public IReadOnlyList<string> EffectiveAllowedCapabilities =>
        AllowedCapabilities is { Count: > 0 } ? AllowedCapabilities : [Capability];

    public IReadOnlyList<string> EffectivePermissionScopes => PermissionScopes ?? [];

    public IReadOnlyList<string> EffectiveRequiredCapabilities =>
        RequiredCapabilities is { Count: > 0 } ? RequiredCapabilities : [Capability];

    public RoutingWeights EffectiveRoutingWeights => RoutingWeights ?? new();
}

public sealed record RoutingWeights(
    decimal Quality = 1m,
    decimal Cost = 4m,
    decimal Latency = 0.00005m,
    decimal Uncertainty = 0.5m);

public sealed record ModelProfile(
    string ProfileId,
    string Capability,
    ModelTier Tier,
    IReadOnlyList<string> SupportedTaskTypes,
    decimal ExpectedQuality,
    decimal EstimatedCost,
    int ExpectedLatencyMilliseconds,
    decimal Uncertainty,
    decimal Reliability,
    decimal Availability,
    int MaxInputTokens,
    int MaxOutputTokens,
    bool Healthy = true,
    string MeasurementSource = "evaluation");

public sealed record CapabilityCandidate(
    string Capability,
    ExecutionStepKind StepKind,
    SideEffectClass SideEffectClass,
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
    decimal Score)
{
    public RoutingCandidateEvaluation ToContract() => new(
        Capability,
        ProfileId,
        ModelTier,
        Eligible,
        Reason,
        ExpectedQuality,
        ExpectedCost,
        ExpectedLatencyMilliseconds,
        Uncertainty,
        Reliability,
        Availability,
        Score);
}

public sealed record CapabilityMatchResult(
    string Capability,
    IReadOnlyList<CapabilityCandidate> Candidates);

public sealed record CapabilitySelection(
    CapabilityCandidate Selected,
    bool Escalated,
    string? EscalationReason);

public sealed record RoutingResult(
    ExecutionPlan Plan,
    RoutingDecision Decision);

public sealed class RoutingException(
    string code,
    string title,
    string message) : Exception(message)
{
    public string Code { get; } = code;
    public string Title { get; } = title;
}
