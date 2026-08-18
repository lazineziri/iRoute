using System.Collections.Concurrent;
using System.Text.Json;
using iRoute.Common;

namespace iRoute.Data;

public sealed partial class InMemoryLifecycleStore(
    InMemoryArtifactStore artifacts,
    InMemoryMemoryStore memories) : ILifecycleStore, IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly ConcurrentDictionary<string, LifecycleArchiveRecord> _archives = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _gate = new(1, 1);

    public async Task<LifecycleSweepResult> SweepAsync(
        LifecyclePolicy policy,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        policy.EnsureValid();
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var before = Snapshot();
            var existingArchives = _archives.Keys.ToHashSet(StringComparer.Ordinal);
            var invalidatedArtifacts = new HashSet<Guid>();
            var invalidatedMemories = new HashSet<Guid>();
            var expiredArtifacts = 0;
            var expiredMemories = 0;

            foreach (var artifact in artifacts.LifecycleSnapshot()
                         .Where(item =>
                             item.IsActive &&
                             item.LifecycleStatus == ArtifactLifecycleStatus.Active &&
                             item.ExpiresAt <= now)
                         .OrderBy(item => item.ExpiresAt)
                         .Take(policy.BatchSize))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!artifacts.LifecycleUpdate(artifact with
                {
                    IsActive = false,
                    LifecycleStatus = ArtifactLifecycleStatus.Invalidated,
                    InvalidatedAt = now,
                    InvalidationReason = "Artifact TTL expired."
                }))
                {
                    continue;
                }

                expiredArtifacts++;
                var propagated = await PropagateAsync(
                    artifact.TenantId,
                    "artifact",
                    artifact.ArtifactId,
                    artifact.ContentHash,
                    "An artifact dependency expired.",
                    now,
                    cancellationToken);
                invalidatedArtifacts.UnionWith(propagated.Artifacts);
                invalidatedMemories.UnionWith(propagated.Memories);
            }

            foreach (var memory in memories.LifecycleSnapshot()
                         .Where(item =>
                             item.LifecycleStatus == MemoryLifecycleStatus.Active &&
                             item.ExpiresAt <= now)
                         .OrderBy(item => item.ExpiresAt)
                         .Take(Math.Max(0, policy.BatchSize - expiredArtifacts)))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!memories.LifecycleUpdate(memory with
                {
                    LifecycleStatus = MemoryLifecycleStatus.Invalidated,
                    InvalidatedAt = now,
                    InvalidationReason = "Memory TTL expired."
                }))
                {
                    continue;
                }

                expiredMemories++;
                var propagated = await PropagateAsync(
                    memory.TenantId,
                    "memory",
                    memory.MemoryId,
                    memory.ContentHash,
                    "A memory dependency expired.",
                    now,
                    cancellationToken);
                invalidatedArtifacts.UnionWith(propagated.Artifacts);
                invalidatedMemories.UnionWith(propagated.Memories);
            }

            var candidates = SelectCandidates(policy, now);
            var protectedDependencies = 0;
            var archivedArtifacts = 0;
            var archivedMemories = 0;
            foreach (var candidate in candidates.Take(policy.BatchSize))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (HasActiveDependent(candidate.TenantId, candidate.Kind, candidate.ResourceId, now))
                {
                    protectedDependencies++;
                    continue;
                }

                var key = ArchiveKey(candidate.TenantId, candidate.Kind, candidate.ResourceId);
                if (_archives.TryAdd(key, new LifecycleArchiveRecord(
                    candidate.TenantId,
                    candidate.Kind,
                    candidate.ResourceId,
                    candidate.ContentHash,
                    candidate.Payload,
                    now)))
                {
                    if (candidate.Kind == LifecycleResourceKind.Artifact)
                    {
                        archivedArtifacts++;
                    }
                    else
                    {
                        archivedMemories++;
                    }
                }
            }

            var deletedArtifacts = 0;
            var deletedMemories = 0;
            foreach (var archive in _archives.Values
                         .Where(item =>
                             existingArchives.Contains(ArchiveKey(
                                 item.TenantId,
                                 item.Kind,
                                 item.ResourceId)) &&
                             item.ArchivedAt + policy.DeleteAfterArchive <= now)
                         .OrderBy(item => item.ArchivedAt)
                         .ThenBy(item => item.ResourceId)
                         .Take(policy.BatchSize))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (HasActiveDependent(archive.TenantId, archive.Kind, archive.ResourceId, now))
                {
                    protectedDependencies++;
                    continue;
                }

                var removed = RemoveResource(archive.TenantId, archive.Kind, archive.ResourceId);
                if (!removed.Deleted)
                {
                    continue;
                }

                if (archive.Kind == LifecycleResourceKind.Artifact)
                {
                    deletedArtifacts++;
                }
                else
                {
                    deletedMemories++;
                }
            }

            var purgedArchives = PurgeArchives(policy, now);
            var after = Snapshot();
            return new LifecycleSweepResult(
                now,
                now,
                expiredArtifacts,
                expiredMemories,
                invalidatedArtifacts.Count,
                invalidatedMemories.Count,
                archivedArtifacts,
                archivedMemories,
                deletedArtifacts,
                deletedMemories,
                purgedArchives,
                protectedDependencies,
                before,
                after);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<LifecycleDeletionResult> DeleteAsync(
        LifecycleDeletionRequest request,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.TenantId) || string.IsNullOrWhiteSpace(request.Reason))
        {
            throw new ArgumentException("TenantId and Reason are required.", nameof(request));
        }

        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (!Exists(request.TenantId, request.ResourceKind, request.ResourceId))
            {
                return new LifecycleDeletionResult(
                    false,
                    request.ResourceKind,
                    request.ResourceId,
                    0,
                    0,
                    0,
                    _archives.TryRemove(ArchiveKey(
                        request.TenantId,
                        request.ResourceKind,
                        request.ResourceId), out _));
            }

            var contentHash = ContentHash(request.ResourceKind, request.ResourceId)!;
            var propagated = await PropagateAsync(
                request.TenantId,
                KindName(request.ResourceKind),
                request.ResourceId,
                contentHash,
                request.Reason,
                request.RequestedAt,
                cancellationToken);
            var removed = RemoveResource(request.TenantId, request.ResourceKind, request.ResourceId);
            var archivePurged = _archives.TryRemove(
                ArchiveKey(request.TenantId, request.ResourceKind, request.ResourceId),
                out _);
            return new LifecycleDeletionResult(
                removed.Deleted,
                request.ResourceKind,
                request.ResourceId,
                propagated.Artifacts.Count,
                propagated.Memories.Count,
                removed.RemovedEdges,
                archivePurged);
        }
        finally
        {
            _gate.Release();
        }
    }

    public Task<LifecycleStorageSnapshot> InspectAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(Snapshot());
    }

}
