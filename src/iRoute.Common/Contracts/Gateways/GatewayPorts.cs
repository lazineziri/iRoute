using System.Runtime.CompilerServices;

namespace iRoute.Common;

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
            TimeProvider.System.GetUtcNow(),
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
