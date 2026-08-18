using System.Text.Json;
using iRoute.Contracts;

namespace iRoute.Core;

public sealed record DependencyChange(
    string TenantId,
    string Kind,
    string Reference,
    string? CurrentContentHash,
    bool SourceRemoved,
    string Reason,
    DateTimeOffset OccurredAt);

public sealed record ArtifactInvalidationResult(IReadOnlyList<Guid> ArtifactIds);

public sealed record MemoryLookup(
    string TenantId,
    string? ProjectId,
    MemoryKind Kind,
    string Key,
    DateTimeOffset At);

public sealed record ActiveMemoryQuery(
    string TenantId,
    string? ProjectId,
    DateTimeOffset At);

public sealed record MemoryRecord(
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
    string? InvalidationReason = null)
{
    public MemorySnapshot ToSnapshot() => new(
        MemoryId,
        TenantId,
        ProjectId,
        Kind,
        Key,
        Version,
        Value,
        ContentHash,
        LifecycleStatus,
        Evidence,
        Dependencies,
        CreatedAt,
        ExpiresAt,
        SupersedesMemoryId,
        SupersededByMemoryId,
        InvalidatedAt,
        InvalidationReason);
}

public sealed record MemoryWriteResult(
    MemoryRecord Record,
    MemoryRecord? Previous,
    bool Created);

public sealed record MemoryInvalidationResult(IReadOnlyList<Guid> MemoryIds);

public interface IMemoryStore
{
    Task<MemoryWriteResult> UpsertAsync(
        MemoryRecord record,
        CancellationToken cancellationToken);

    Task<MemoryRecord?> GetActiveAsync(
        MemoryLookup query,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<MemoryRecord>> ListActiveAsync(
        ActiveMemoryQuery query,
        CancellationToken cancellationToken);

    Task<MemoryRecord?> GetAsync(
        string tenantId,
        Guid memoryId,
        CancellationToken cancellationToken);

    Task<MemoryInvalidationResult> InvalidateByDependencyAsync(
        DependencyChange change,
        CancellationToken cancellationToken);
}
