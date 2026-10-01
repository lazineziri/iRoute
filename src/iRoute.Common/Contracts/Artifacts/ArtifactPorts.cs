using System.Text.Json;

namespace iRoute.Common;

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
    string? InvalidationReason = null,
    decimal? Confidence = null)
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
        InvalidationReason,
        Confidence);
}
