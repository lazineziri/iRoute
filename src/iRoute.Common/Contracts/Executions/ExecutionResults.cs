using System.Text.Json;

namespace iRoute.Common;

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
