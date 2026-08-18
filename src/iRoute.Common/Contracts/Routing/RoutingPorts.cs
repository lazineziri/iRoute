
namespace iRoute.Common;

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
    ModelProfileSource MeasurementSource = ModelProfileSource.Synthetic,
    ModelProfileMeasurement? Measurement = null);

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
    decimal Score,
    ModelProfileSource? MeasurementSource = null,
    ModelProfileMeasurement? Measurement = null)
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
        Score,
        MeasurementSource,
        Measurement);
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
