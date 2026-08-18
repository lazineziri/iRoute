using System.Text.Json;

namespace iRoute.Common;

public sealed record ModelGatewayRequest(
    string Capability,
    JsonElement Input,
    JsonElement Context,
    int MaxOutputTokens,
    string? CorrelationId = null,
    string? ProfileId = null,
    int? DeadlineMilliseconds = null,
    decimal? MinimumQuality = null,
    decimal? MaximumCost = null,
    IReadOnlyList<string>? AllowedRegions = null,
    string? RequiredResidency = null,
    int? MaximumAttempts = null);

public sealed record ModelGatewayResult(
    JsonElement Output,
    UsageSummary Usage,
    decimal Confidence,
    IReadOnlyList<EvidenceReference> Evidence,
    string? GatewayId = null,
    ModelGatewayTransport Transport = ModelGatewayTransport.Buffered,
    ModelGatewayFinishReason FinishReason = ModelGatewayFinishReason.Completed,
    GatewayDeploymentReference? Deployment = null,
    GatewayResilienceTrace? Resilience = null);

public sealed record ModelGatewayStreamEvent(
    long Sequence,
    ModelGatewayStreamEventKind Kind,
    string? Delta = null,
    UsageSummary? Usage = null,
    ModelGatewayResult? Result = null);

public sealed record ModelGatewayHealth(
    string GatewayId,
    ModelGatewayHealthStatus Status,
    long LatencyMilliseconds,
    DateTimeOffset CheckedAt,
    string? Message = null);

public sealed record ModelGatewayFailure(
    string Code,
    ModelGatewayFailureKind Kind,
    string Message,
    bool Retryable,
    int? StatusCode = null,
    string? GatewayId = null,
    string? CorrelationId = null,
    int? RetryAfterMilliseconds = null,
    GatewayFailureClass? FailureClass = null,
    GatewayResilienceTrace? Resilience = null);

public sealed record GatewayDeploymentReference(
    string GatewayId,
    string Provider,
    string DeploymentId,
    string Region,
    string Residency,
    string ModelVersion);

public sealed record GatewayCandidateEvidence(
    GatewayDeploymentReference Deployment,
    bool Eligible,
    string Reason,
    GatewayCircuitState CircuitState,
    GatewayFailureClass? FailureClass = null);

public sealed record GatewayAttemptEvidence(
    GatewayDeploymentReference Deployment,
    int Attempt,
    GatewayCircuitState CircuitStateBefore,
    GatewayCircuitState CircuitStateAfter,
    bool Succeeded,
    GatewayFailureClass? FailureClass,
    string? FailureCode,
    int? StatusCode,
    long DurationMilliseconds,
    int? RetryAfterMilliseconds = null);

public sealed record GatewayResilienceTrace(
    string PolicyVersion,
    IReadOnlyList<GatewayCandidateEvidence> Candidates,
    IReadOnlyList<GatewayAttemptEvidence> Attempts,
    GatewayDeploymentReference? FinalDeployment,
    string? FallbackReason,
    string? ExhaustionReason);

public sealed record UsageSummary(
    int InputTokens = 0,
    int OutputTokens = 0,
    decimal Cost = 0,
    long DurationMilliseconds = 0,
    int ModelCalls = 0,
    int ToolCalls = 0);
