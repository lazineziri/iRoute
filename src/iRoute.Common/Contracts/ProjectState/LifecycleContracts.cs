namespace iRoute.Common;

public enum LifecycleResourceKind
{
    Artifact,
    Memory
}

public sealed record LifecyclePolicy
{
    public TimeSpan SweepInterval { get; init; } = TimeSpan.FromMinutes(5);
    public TimeSpan DefaultArtifactTimeToLive { get; init; } = TimeSpan.FromDays(30);
    public TimeSpan DefaultMemoryTimeToLive { get; init; } = TimeSpan.FromDays(90);
    public TimeSpan ArchiveAfterInactive { get; init; } = TimeSpan.FromDays(7);
    public TimeSpan DeleteAfterArchive { get; init; } = TimeSpan.FromDays(7);
    public TimeSpan ArchiveRetention { get; init; } = TimeSpan.FromDays(30);
    public int MaxArtifactVersionsPerLineage { get; init; } = 5;
    public int MaxMemoryVersionsPerLineage { get; init; } = 5;
    public int MaxArtifactsPerTenant { get; init; } = 10_000;
    public int MaxMemoryRecordsPerTenant { get; init; } = 10_000;
    public int MaxArchivesPerTenant { get; init; } = 20_000;
    public int BatchSize { get; init; } = 500;

    public void EnsureValid()
    {
        if (SweepInterval <= TimeSpan.Zero ||
            DefaultArtifactTimeToLive <= TimeSpan.Zero ||
            DefaultMemoryTimeToLive <= TimeSpan.Zero ||
            ArchiveAfterInactive < TimeSpan.Zero ||
            DeleteAfterArchive < TimeSpan.Zero ||
            ArchiveRetention < TimeSpan.Zero ||
            MaxArtifactVersionsPerLineage < 1 ||
            MaxMemoryVersionsPerLineage < 1 ||
            MaxArtifactsPerTenant < 1 ||
            MaxMemoryRecordsPerTenant < 1 ||
            MaxArchivesPerTenant < 1 ||
            BatchSize < 1)
        {
            throw new InvalidOperationException("Lifecycle intervals and quotas must be positive; retention delays may be zero.");
        }
    }
}

public sealed record LifecycleStorageSnapshot(
    int ArtifactCount,
    int MemoryCount,
    int ArchiveCount,
    int DependencyEdgeCount,
    int DanglingDependencyEdgeCount);

public sealed record LifecycleSweepResult(
    DateTimeOffset StartedAt,
    DateTimeOffset CompletedAt,
    int ExpiredArtifacts,
    int ExpiredMemoryRecords,
    int InvalidatedArtifacts,
    int InvalidatedMemoryRecords,
    int ArchivedArtifacts,
    int ArchivedMemoryRecords,
    int DeletedArtifacts,
    int DeletedMemoryRecords,
    int PurgedArchives,
    int ProtectedActiveDependencies,
    LifecycleStorageSnapshot Before,
    LifecycleStorageSnapshot After);

public sealed record LifecycleDeletionRequest(
    string TenantId,
    LifecycleResourceKind ResourceKind,
    Guid ResourceId,
    string Reason,
    DateTimeOffset RequestedAt);

public sealed record LifecycleDeletionResult(
    bool Deleted,
    LifecycleResourceKind ResourceKind,
    Guid ResourceId,
    int InvalidatedArtifacts,
    int InvalidatedMemoryRecords,
    int RemovedDependencyEdges,
    bool ArchivePurged);

public interface ILifecycleStore
{
    Task<LifecycleSweepResult> SweepAsync(
        LifecyclePolicy policy,
        DateTimeOffset now,
        CancellationToken cancellationToken);

    Task<LifecycleDeletionResult> DeleteAsync(
        LifecycleDeletionRequest request,
        CancellationToken cancellationToken);

    Task<LifecycleStorageSnapshot> InspectAsync(CancellationToken cancellationToken);
}
