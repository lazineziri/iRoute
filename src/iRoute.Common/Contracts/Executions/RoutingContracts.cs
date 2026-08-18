namespace iRoute.Common;

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
    IReadOnlyList<RoutingCandidateEvaluation> Candidates,
    GatewayDeploymentReference? SelectedDeployment = null,
    GatewayResilienceTrace? Resilience = null);

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
    decimal Score,
    ModelProfileSource? MeasurementSource = null,
    ModelProfileMeasurement? Measurement = null);

public sealed record ModelProfileMeasurement(
    string Provider,
    string Model,
    DateTimeOffset MeasuredAt,
    int SampleCount,
    bool QualityIsDeclaredNotMeasured = true);
