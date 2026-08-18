using System.Text.Json;

namespace iRoute.Common;

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
