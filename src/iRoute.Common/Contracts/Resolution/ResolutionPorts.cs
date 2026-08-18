using System.Text.Json;

namespace iRoute.Common;

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
