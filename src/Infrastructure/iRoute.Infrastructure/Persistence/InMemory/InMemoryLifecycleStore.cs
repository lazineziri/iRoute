using System.Collections.Concurrent;
using System.Text.Json;
using iRoute.Contracts;
using iRoute.Core;

namespace iRoute.Infrastructure;

public sealed class InMemoryLifecycleStore(
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

    private LifecycleCandidate[] SelectCandidates(
        LifecyclePolicy policy,
        DateTimeOffset now)
    {
        var artifactsSnapshot = artifacts.LifecycleSnapshot();
        var memorySnapshot = memories.LifecycleSnapshot();
        var candidates = new Dictionary<string, LifecycleCandidate>(StringComparer.Ordinal);
        var inactiveCutoff = now - policy.ArchiveAfterInactive;

        foreach (var artifact in artifactsSnapshot.Where(item => !IsActive(item, now)))
        {
            var inactiveAt = artifact.InvalidatedAt ?? artifact.CreatedAt;
            if (inactiveAt <= inactiveCutoff)
            {
                Add(ToCandidate(artifact));
            }
        }

        foreach (var memory in memorySnapshot.Where(item => !IsActive(item, now)))
        {
            var inactiveAt = memory.InvalidatedAt ?? memory.CreatedAt;
            if (inactiveAt <= inactiveCutoff)
            {
                Add(ToCandidate(memory));
            }
        }

        foreach (var lineage in artifactsSnapshot.GroupBy(
                     item => (item.TenantId, item.ProjectId, item.ArtifactType, item.EffectiveLogicalKey)))
        {
            foreach (var artifact in lineage
                         .OrderByDescending(item => item.Version)
                         .ThenByDescending(item => item.ArtifactId)
                         .Skip(policy.MaxArtifactVersionsPerLineage)
                         .Where(item => !IsActive(item, now)))
            {
                Add(ToCandidate(artifact));
            }
        }

        foreach (var lineage in memorySnapshot.GroupBy(
                     item => (item.TenantId, item.ProjectId, item.Kind, item.Key)))
        {
            foreach (var memory in lineage
                         .OrderByDescending(item => item.Version)
                         .ThenByDescending(item => item.MemoryId)
                         .Skip(policy.MaxMemoryVersionsPerLineage)
                         .Where(item => !IsActive(item, now)))
            {
                Add(ToCandidate(memory));
            }
        }

        AddTenantQuotaCandidates(
            artifactsSnapshot.Select(ToCandidate),
            policy.MaxArtifactsPerTenant,
            now,
            Add);
        AddTenantQuotaCandidates(
            memorySnapshot.Select(ToCandidate),
            policy.MaxMemoryRecordsPerTenant,
            now,
            Add);
        return candidates.Values
            .OrderBy(item => item.CreatedAt)
            .ThenBy(item => item.Kind)
            .ThenBy(item => item.ResourceId)
            .ToArray();

        void Add(LifecycleCandidate candidate) =>
            candidates.TryAdd(
                ArchiveKey(candidate.TenantId, candidate.Kind, candidate.ResourceId),
                candidate);
    }

    private static void AddTenantQuotaCandidates(
        IEnumerable<LifecycleCandidate> source,
        int maximum,
        DateTimeOffset now,
        Action<LifecycleCandidate> add)
    {
        foreach (var tenant in source.GroupBy(item => item.TenantId, StringComparer.Ordinal))
        {
            var values = tenant.ToArray();
            var overflow = values.Length - maximum;
            if (overflow <= 0)
            {
                continue;
            }

            foreach (var candidate in values
                         .Where(item => !item.Active || item.ExpiresAt <= now)
                         .OrderBy(item => item.CreatedAt)
                         .ThenBy(item => item.ResourceId)
                         .Take(overflow))
            {
                add(candidate);
            }
        }
    }

    private bool HasActiveDependent(
        string tenantId,
        LifecycleResourceKind kind,
        Guid resourceId,
        DateTimeOffset now)
    {
        var targetKind = KindName(kind);
        var targetReference = resourceId.ToString();
        return artifacts.LifecycleSnapshot().Any(item =>
                   string.Equals(item.TenantId, tenantId, StringComparison.Ordinal) &&
                   IsActive(item, now) &&
                   item.EffectiveDependencies.Any(dependency => Matches(dependency, targetKind, targetReference))) ||
               memories.LifecycleSnapshot().Any(item =>
                   string.Equals(item.TenantId, tenantId, StringComparison.Ordinal) &&
                   IsActive(item, now) &&
                   item.Dependencies.Any(dependency => Matches(dependency, targetKind, targetReference)));
    }

    private async Task<PropagationResult> PropagateAsync(
        string tenantId,
        string kind,
        Guid resourceId,
        string contentHash,
        string reason,
        DateTimeOffset at,
        CancellationToken cancellationToken)
    {
        var change = new DependencyChange(
            tenantId,
            kind,
            resourceId.ToString(),
            contentHash,
            true,
            reason,
            at);
        var memoryResult = await memories.InvalidateByDependencyAsync(change, cancellationToken);
        var artifactIds = new HashSet<Guid>((await artifacts.InvalidateByDependencyAsync(
            change,
            cancellationToken)).ArtifactIds);
        foreach (var memoryId in memoryResult.MemoryIds)
        {
            var dependent = await artifacts.InvalidateByDependencyAsync(
                change with
                {
                    Kind = "memory",
                    Reference = memoryId.ToString(),
                    CurrentContentHash = null
                },
                cancellationToken);
            artifactIds.UnionWith(dependent.ArtifactIds);
        }

        return new PropagationResult(artifactIds, memoryResult.MemoryIds.ToHashSet());
    }

    private ResourceRemoval RemoveResource(
        string tenantId,
        LifecycleResourceKind kind,
        Guid resourceId)
    {
        if (!Exists(tenantId, kind, resourceId))
        {
            return new ResourceRemoval(false, 0);
        }

        var targetKind = KindName(kind);
        var targetReference = resourceId.ToString();
        var removedEdges = 0;
        foreach (var artifact in artifacts.LifecycleSnapshot()
                     .Where(item => string.Equals(item.TenantId, tenantId, StringComparison.Ordinal)))
        {
            var dependencies = artifact.EffectiveDependencies
                .Where(item => !Matches(item, targetKind, targetReference))
                .ToArray();
            removedEdges += artifact.EffectiveDependencies.Count - dependencies.Length;
            var updated = artifact with
            {
                Dependencies = dependencies,
                SupersedesArtifactId = artifact.SupersedesArtifactId == resourceId
                    ? null
                    : artifact.SupersedesArtifactId,
                SupersededByArtifactId = artifact.SupersededByArtifactId == resourceId
                    ? null
                    : artifact.SupersededByArtifactId
            };
            artifacts.LifecycleUpdate(updated);
        }

        foreach (var memory in memories.LifecycleSnapshot()
                     .Where(item => string.Equals(item.TenantId, tenantId, StringComparison.Ordinal)))
        {
            var dependencies = memory.Dependencies
                .Where(item => !Matches(item, targetKind, targetReference))
                .ToArray();
            removedEdges += memory.Dependencies.Count - dependencies.Length;
            var updated = memory with
            {
                Dependencies = dependencies,
                SupersedesMemoryId = memory.SupersedesMemoryId == resourceId
                    ? null
                    : memory.SupersedesMemoryId,
                SupersededByMemoryId = memory.SupersededByMemoryId == resourceId
                    ? null
                    : memory.SupersededByMemoryId
            };
            memories.LifecycleUpdate(updated);
        }

        bool deleted;
        if (kind == LifecycleResourceKind.Artifact)
        {
            var resource = artifacts.LifecycleSnapshot().Single(item => item.ArtifactId == resourceId);
            removedEdges += resource.EffectiveDependencies.Count;
            deleted = artifacts.LifecycleRemove(resourceId);
        }
        else
        {
            var resource = memories.LifecycleSnapshot().Single(item => item.MemoryId == resourceId);
            removedEdges += resource.Dependencies.Count;
            deleted = memories.LifecycleRemove(resourceId);
        }

        return new ResourceRemoval(deleted, removedEdges);
    }

    private int PurgeArchives(LifecyclePolicy policy, DateTimeOffset now)
    {
        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var archive in _archives.Values.Where(item =>
                     !Exists(item.TenantId, item.Kind, item.ResourceId) &&
                     item.ArchivedAt + policy.ArchiveRetention <= now))
        {
            keys.Add(ArchiveKey(archive.TenantId, archive.Kind, archive.ResourceId));
        }

        foreach (var tenant in _archives.Values.GroupBy(item => item.TenantId, StringComparer.Ordinal))
        {
            var overflow = tenant.Count() - policy.MaxArchivesPerTenant;
            if (overflow <= 0)
            {
                continue;
            }

            foreach (var archive in tenant
                         .Where(item => !Exists(item.TenantId, item.Kind, item.ResourceId))
                         .OrderBy(item => item.ArchivedAt)
                         .ThenBy(item => item.ResourceId)
                         .Take(overflow))
            {
                keys.Add(ArchiveKey(archive.TenantId, archive.Kind, archive.ResourceId));
            }
        }

        return keys.Count(key => _archives.TryRemove(key, out _));
    }

    private LifecycleStorageSnapshot Snapshot()
    {
        var artifactSnapshot = artifacts.LifecycleSnapshot();
        var memorySnapshot = memories.LifecycleSnapshot();
        var edgeCount = artifactSnapshot.Sum(item => item.EffectiveDependencies.Count) +
            memorySnapshot.Sum(item => item.Dependencies.Count);
        var dangling = artifactSnapshot.SelectMany(item => item.EffectiveDependencies)
            .Concat(memorySnapshot.SelectMany(item => item.Dependencies))
            .Count(IsDangling);
        return new LifecycleStorageSnapshot(
            artifactSnapshot.Count,
            memorySnapshot.Count,
            _archives.Count,
            edgeCount,
            dangling);
    }

    private bool IsDangling(DependencyReference dependency)
    {
        if (!Guid.TryParse(dependency.Reference, out var id))
        {
            return false;
        }

        return dependency.Kind switch
        {
            "artifact" => artifacts.LifecycleSnapshot().All(item => item.ArtifactId != id),
            "memory" => memories.LifecycleSnapshot().All(item => item.MemoryId != id),
            _ => false
        };
    }

    private bool Exists(string tenantId, LifecycleResourceKind kind, Guid resourceId) => kind switch
    {
        LifecycleResourceKind.Artifact => artifacts.LifecycleSnapshot().Any(item =>
            item.ArtifactId == resourceId && string.Equals(item.TenantId, tenantId, StringComparison.Ordinal)),
        _ => memories.LifecycleSnapshot().Any(item =>
            item.MemoryId == resourceId && string.Equals(item.TenantId, tenantId, StringComparison.Ordinal))
    };

    private string? ContentHash(LifecycleResourceKind kind, Guid resourceId) => kind switch
    {
        LifecycleResourceKind.Artifact => artifacts.LifecycleSnapshot()
            .FirstOrDefault(item => item.ArtifactId == resourceId)?.ContentHash,
        _ => memories.LifecycleSnapshot()
            .FirstOrDefault(item => item.MemoryId == resourceId)?.ContentHash
    };

    private static bool IsActive(ArtifactRecord artifact, DateTimeOffset now) =>
        artifact.IsActive &&
        artifact.LifecycleStatus == ArtifactLifecycleStatus.Active &&
        (artifact.ExpiresAt is null || artifact.ExpiresAt > now);

    private static bool IsActive(MemoryRecord memory, DateTimeOffset now) =>
        memory.LifecycleStatus == MemoryLifecycleStatus.Active &&
        (memory.ExpiresAt is null || memory.ExpiresAt > now);

    private static LifecycleCandidate ToCandidate(ArtifactRecord artifact) => new(
        LifecycleResourceKind.Artifact,
        artifact.ArtifactId,
        artifact.TenantId,
        artifact.Version,
        artifact.CreatedAt,
        artifact.ExpiresAt,
        artifact.IsActive && artifact.LifecycleStatus == ArtifactLifecycleStatus.Active,
        artifact.ContentHash,
        JsonSerializer.SerializeToElement(artifact, JsonOptions));

    private static LifecycleCandidate ToCandidate(MemoryRecord memory) => new(
        LifecycleResourceKind.Memory,
        memory.MemoryId,
        memory.TenantId,
        memory.Version,
        memory.CreatedAt,
        memory.ExpiresAt,
        memory.LifecycleStatus == MemoryLifecycleStatus.Active,
        memory.ContentHash,
        JsonSerializer.SerializeToElement(memory, JsonOptions));

    private static bool Matches(
        DependencyReference dependency,
        string targetKind,
        string targetReference) =>
        string.Equals(dependency.Kind, targetKind, StringComparison.Ordinal) &&
        string.Equals(dependency.Reference, targetReference, StringComparison.Ordinal);

    private static string KindName(LifecycleResourceKind kind) => kind switch
    {
        LifecycleResourceKind.Artifact => "artifact",
        _ => "memory"
    };

    private static string ArchiveKey(
        string tenantId,
        LifecycleResourceKind kind,
        Guid resourceId) => $"{tenantId}:{kind}:{resourceId}";

    public void Dispose() => _gate.Dispose();

    private sealed record LifecycleCandidate(
        LifecycleResourceKind Kind,
        Guid ResourceId,
        string TenantId,
        int Version,
        DateTimeOffset CreatedAt,
        DateTimeOffset? ExpiresAt,
        bool Active,
        string ContentHash,
        JsonElement Payload);

    private sealed record LifecycleArchiveRecord(
        string TenantId,
        LifecycleResourceKind Kind,
        Guid ResourceId,
        string ContentHash,
        JsonElement Payload,
        DateTimeOffset ArchivedAt);

    private sealed record PropagationResult(
        HashSet<Guid> Artifacts,
        HashSet<Guid> Memories);

    private sealed record ResourceRemoval(bool Deleted, int RemovedEdges);
}
