using System.Data;
using System.Text.Json;
using iRoute.Contracts;
using iRoute.Core;
using Microsoft.EntityFrameworkCore;

namespace iRoute.Infrastructure;

public sealed class EfLifecycleStore(IDbContextFactory<IRouteDbContext> contextFactory) : ILifecycleStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<LifecycleSweepResult> SweepAsync(
        LifecyclePolicy policy,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        policy.EnsureValid();
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await context.Database.BeginTransactionAsync(
            IsolationLevel.Serializable,
            cancellationToken);
        var before = await SnapshotAsync(context, cancellationToken);
        var existingArchives = (await context.LifecycleArchives
                .AsNoTracking()
                .Select(item => new { item.TenantId, item.ResourceKind, item.ResourceId })
                .ToArrayAsync(cancellationToken))
            .Select(item => ArchiveKey(item.TenantId, item.ResourceKind, item.ResourceId))
            .ToHashSet(StringComparer.Ordinal);
        var nowMilliseconds = now.ToUnixTimeMilliseconds();
        var invalidatedArtifacts = new HashSet<Guid>();
        var invalidatedMemories = new HashSet<Guid>();

        var expiredArtifacts = await context.Artifacts
            .Where(item =>
                item.IsActive &&
                item.LifecycleStatus == ArtifactLifecycleStatus.Active &&
                item.ExpiresAtUnixMilliseconds != null &&
                item.ExpiresAtUnixMilliseconds <= nowMilliseconds)
            .OrderBy(item => item.ExpiresAtUnixMilliseconds)
            .Take(policy.BatchSize)
            .ToListAsync(cancellationToken);
        foreach (var artifact in expiredArtifacts)
        {
            artifact.IsActive = false;
            artifact.LifecycleStatus = ArtifactLifecycleStatus.Invalidated;
            artifact.InvalidatedAtUnixMilliseconds = nowMilliseconds;
            artifact.InvalidationReason = "Artifact TTL expired.";
            var propagated = await InvalidateDependentsAsync(
                context,
                artifact.TenantId,
                "artifact",
                artifact.ArtifactId,
                "An artifact dependency expired.",
                now,
                cancellationToken);
            invalidatedArtifacts.UnionWith(propagated.Artifacts);
            invalidatedMemories.UnionWith(propagated.Memories);
        }

        var memoryLimit = Math.Max(0, policy.BatchSize - expiredArtifacts.Count);
        var expiredMemories = await context.MemoryRecords
            .Where(item =>
                item.LifecycleStatus == MemoryLifecycleStatus.Active &&
                item.ExpiresAtUnixMilliseconds != null &&
                item.ExpiresAtUnixMilliseconds <= nowMilliseconds)
            .OrderBy(item => item.ExpiresAtUnixMilliseconds)
            .Take(memoryLimit)
            .ToListAsync(cancellationToken);
        foreach (var memory in expiredMemories)
        {
            memory.LifecycleStatus = MemoryLifecycleStatus.Invalidated;
            memory.InvalidatedAtUnixMilliseconds = nowMilliseconds;
            memory.InvalidationReason = "Memory TTL expired.";
            var propagated = await InvalidateDependentsAsync(
                context,
                memory.TenantId,
                "memory",
                memory.MemoryId,
                "A memory dependency expired.",
                now,
                cancellationToken);
            invalidatedArtifacts.UnionWith(propagated.Artifacts);
            invalidatedMemories.UnionWith(propagated.Memories);
        }

        await context.SaveChangesAsync(cancellationToken);
        var candidates = await SelectCandidatesAsync(context, policy, now, cancellationToken);
        var protectedDependencies = 0;
        var archivedArtifacts = 0;
        var archivedMemories = 0;
        foreach (var candidate in candidates.Take(policy.BatchSize))
        {
            if (await HasActiveDependentAsync(context, candidate, now, cancellationToken))
            {
                protectedDependencies++;
                continue;
            }

            var exists = await context.LifecycleArchives.AnyAsync(
                item => item.TenantId == candidate.TenantId &&
                    item.ResourceKind == candidate.Kind &&
                    item.ResourceId == candidate.ResourceId,
                cancellationToken);
            if (exists)
            {
                continue;
            }

            context.LifecycleArchives.Add(new LifecycleArchiveEntity
            {
                TenantId = candidate.TenantId,
                ResourceKind = candidate.Kind,
                ResourceId = candidate.ResourceId,
                ContentHash = candidate.ContentHash,
                PayloadJson = await SerializePayloadAsync(context, candidate, cancellationToken),
                ArchivedAtUnixMilliseconds = nowMilliseconds
            });
            if (candidate.Kind == LifecycleResourceKind.Artifact)
            {
                archivedArtifacts++;
            }
            else
            {
                archivedMemories++;
            }
        }

        await context.SaveChangesAsync(cancellationToken);
        var deleteCutoff = now.Subtract(policy.DeleteAfterArchive).ToUnixTimeMilliseconds();
        var deletableArchives = await context.LifecycleArchives
            .Where(item => item.ArchivedAtUnixMilliseconds <= deleteCutoff)
            .OrderBy(item => item.ArchivedAtUnixMilliseconds)
            .ThenBy(item => item.ResourceId)
            .Take(policy.BatchSize)
            .ToListAsync(cancellationToken);
        var deletedArtifacts = 0;
        var deletedMemories = 0;
        foreach (var archive in deletableArchives.Where(item =>
                     existingArchives.Contains(ArchiveKey(
                         item.TenantId,
                         item.ResourceKind,
                         item.ResourceId))))
        {
            var candidate = new LifecycleCandidate(
                archive.ResourceKind,
                archive.ResourceId,
                archive.TenantId,
                0,
                DateTimeOffset.FromUnixTimeMilliseconds(archive.ArchivedAtUnixMilliseconds),
                null,
                false,
                archive.ContentHash);
            if (await HasActiveDependentAsync(context, candidate, now, cancellationToken))
            {
                protectedDependencies++;
                continue;
            }

            var removed = await RemoveResourceAsync(
                context,
                archive.TenantId,
                archive.ResourceKind,
                archive.ResourceId,
                cancellationToken);
            if (removed.Deleted && archive.ResourceKind == LifecycleResourceKind.Artifact)
            {
                deletedArtifacts++;
            }
            else if (removed.Deleted)
            {
                deletedMemories++;
            }
        }

        await context.SaveChangesAsync(cancellationToken);
        var purgedArchives = await PurgeArchivesAsync(context, policy, now, cancellationToken);
        await context.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        var after = await InspectAsync(cancellationToken);
        return new LifecycleSweepResult(
            now,
            now,
            expiredArtifacts.Count,
            expiredMemories.Count,
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

    public async Task<LifecycleDeletionResult> DeleteAsync(
        LifecycleDeletionRequest request,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.TenantId) || string.IsNullOrWhiteSpace(request.Reason))
        {
            throw new ArgumentException("TenantId and Reason are required.", nameof(request));
        }

        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await context.Database.BeginTransactionAsync(
            IsolationLevel.Serializable,
            cancellationToken);
        var exists = await ResourceExistsAsync(
            context,
            request.TenantId,
            request.ResourceKind,
            request.ResourceId,
            cancellationToken);
        var archive = await context.LifecycleArchives.SingleOrDefaultAsync(
            item => item.TenantId == request.TenantId &&
                item.ResourceKind == request.ResourceKind &&
                item.ResourceId == request.ResourceId,
            cancellationToken);
        if (!exists)
        {
            if (archive is not null)
            {
                context.LifecycleArchives.Remove(archive);
                await context.SaveChangesAsync(cancellationToken);
            }

            await transaction.CommitAsync(cancellationToken);
            return new LifecycleDeletionResult(
                false,
                request.ResourceKind,
                request.ResourceId,
                0,
                0,
                0,
                archive is not null);
        }

        var propagated = await InvalidateDependentsAsync(
            context,
            request.TenantId,
            KindName(request.ResourceKind),
            request.ResourceId,
            request.Reason,
            request.RequestedAt,
            cancellationToken);
        var removed = await RemoveResourceAsync(
            context,
            request.TenantId,
            request.ResourceKind,
            request.ResourceId,
            cancellationToken);
        if (archive is not null)
        {
            context.LifecycleArchives.Remove(archive);
        }

        await context.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new LifecycleDeletionResult(
            removed.Deleted,
            request.ResourceKind,
            request.ResourceId,
            propagated.Artifacts.Count,
            propagated.Memories.Count,
            removed.RemovedEdges,
            archive is not null);
    }

    public async Task<LifecycleStorageSnapshot> InspectAsync(CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        return await SnapshotAsync(context, cancellationToken);
    }

    private static async Task<IReadOnlyList<LifecycleCandidate>> SelectCandidatesAsync(
        IRouteDbContext context,
        LifecyclePolicy policy,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var artifacts = await context.Artifacts.AsNoTracking().ToArrayAsync(cancellationToken);
        var memories = await context.MemoryRecords.AsNoTracking().ToArrayAsync(cancellationToken);
        var selected = new Dictionary<string, LifecycleCandidate>(StringComparer.Ordinal);
        var inactiveCutoff = now.Subtract(policy.ArchiveAfterInactive).ToUnixTimeMilliseconds();
        foreach (var artifact in artifacts.Where(item =>
                     !IsActive(item, now) &&
                     (item.InvalidatedAtUnixMilliseconds ?? item.CreatedAtUnixMilliseconds) <= inactiveCutoff))
        {
            Add(ToCandidate(artifact));
        }

        foreach (var memory in memories.Where(item =>
                     !IsActive(item, now) &&
                     (item.InvalidatedAtUnixMilliseconds ?? item.CreatedAtUnixMilliseconds) <= inactiveCutoff))
        {
            Add(ToCandidate(memory));
        }

        foreach (var lineage in artifacts.GroupBy(
                     item => (item.TenantId, item.ProjectId, item.ArtifactType, item.LogicalKey)))
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

        foreach (var lineage in memories.GroupBy(item =>
                     (item.TenantId, item.ProjectId, item.Kind, item.Key)))
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
            artifacts.Select(ToCandidate),
            policy.MaxArtifactsPerTenant,
            now,
            Add);
        AddTenantQuotaCandidates(
            memories.Select(ToCandidate),
            policy.MaxMemoryRecordsPerTenant,
            now,
            Add);
        return selected.Values
            .OrderBy(item => item.CreatedAt)
            .ThenBy(item => item.Kind)
            .ThenBy(item => item.ResourceId)
            .ToArray();

        void Add(LifecycleCandidate candidate) =>
            selected.TryAdd(
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
                         .Where(item => !CandidateIsActive(item, now))
                         .OrderBy(item => item.CreatedAt)
                         .ThenBy(item => item.ResourceId)
                         .Take(overflow))
            {
                add(candidate);
            }
        }

        static bool CandidateIsActive(LifecycleCandidate candidate, DateTimeOffset at) =>
            candidate.Active && (candidate.ExpiresAt is null || candidate.ExpiresAt > at);
    }

    private static async Task<bool> HasActiveDependentAsync(
        IRouteDbContext context,
        LifecycleCandidate candidate,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var sourceIds = await context.DependencyEdges
            .AsNoTracking()
            .Where(item =>
                item.TenantId == candidate.TenantId &&
                item.TargetKind == KindName(candidate.Kind) &&
                item.TargetReference == candidate.ResourceId.ToString())
            .Select(item => new { item.SourceKind, item.SourceId })
            .ToArrayAsync(cancellationToken);
        var artifactIds = sourceIds
            .Where(item => item.SourceKind == "artifact")
            .Select(item => item.SourceId)
            .ToArray();
        var memoryIds = sourceIds
            .Where(item => item.SourceKind == "memory")
            .Select(item => item.SourceId)
            .ToArray();
        var nowMilliseconds = now.ToUnixTimeMilliseconds();
        return await context.Artifacts.AsNoTracking().AnyAsync(item =>
                   artifactIds.Contains(item.ArtifactId) &&
                   item.IsActive &&
                   item.LifecycleStatus == ArtifactLifecycleStatus.Active &&
                   (item.ExpiresAtUnixMilliseconds == null || item.ExpiresAtUnixMilliseconds > nowMilliseconds),
                   cancellationToken) ||
               await context.MemoryRecords.AsNoTracking().AnyAsync(item =>
                   memoryIds.Contains(item.MemoryId) &&
                   item.LifecycleStatus == MemoryLifecycleStatus.Active &&
                   (item.ExpiresAtUnixMilliseconds == null || item.ExpiresAtUnixMilliseconds > nowMilliseconds),
                   cancellationToken);
    }

    private static async Task<PropagationResult> InvalidateDependentsAsync(
        IRouteDbContext context,
        string tenantId,
        string kind,
        Guid resourceId,
        string reason,
        DateTimeOffset at,
        CancellationToken cancellationToken)
    {
        var artifactIds = new HashSet<Guid>();
        var memoryIds = new HashSet<Guid>();
        var seenArtifactIds = new HashSet<Guid>();
        var seenMemoryIds = new HashSet<Guid>();
        var queue = new Queue<(string Kind, string Reference)>();
        queue.Enqueue((kind, resourceId.ToString()));
        while (queue.TryDequeue(out var target))
        {
            var edges = await context.DependencyEdges
                .AsNoTracking()
                .Where(item =>
                    item.TenantId == tenantId &&
                    item.TargetKind == target.Kind &&
                    item.TargetReference == target.Reference)
                .Select(item => new { item.SourceKind, item.SourceId })
                .ToArrayAsync(cancellationToken);
            var candidateMemoryIds = edges
                .Where(item => item.SourceKind == "memory")
                .Select(item => item.SourceId)
                .Where(seenMemoryIds.Add)
                .ToArray();
            var candidateArtifactIds = edges
                .Where(item => item.SourceKind == "artifact")
                .Select(item => item.SourceId)
                .Where(seenArtifactIds.Add)
                .ToArray();
            var dependentMemories = await context.MemoryRecords
                .Where(item =>
                    candidateMemoryIds.Contains(item.MemoryId) &&
                    item.LifecycleStatus == MemoryLifecycleStatus.Active)
                .ToListAsync(cancellationToken);
            foreach (var memory in dependentMemories)
            {
                memory.LifecycleStatus = MemoryLifecycleStatus.Invalidated;
                memory.InvalidatedAtUnixMilliseconds = at.ToUnixTimeMilliseconds();
                memory.InvalidationReason = reason;
                memoryIds.Add(memory.MemoryId);
                queue.Enqueue(("memory", memory.MemoryId.ToString()));
            }

            var dependentArtifacts = await context.Artifacts
                .Where(item =>
                    candidateArtifactIds.Contains(item.ArtifactId) &&
                    item.IsActive &&
                    item.LifecycleStatus == ArtifactLifecycleStatus.Active)
                .ToListAsync(cancellationToken);
            foreach (var artifact in dependentArtifacts)
            {
                artifact.IsActive = false;
                artifact.LifecycleStatus = ArtifactLifecycleStatus.Invalidated;
                artifact.InvalidatedAtUnixMilliseconds = at.ToUnixTimeMilliseconds();
                artifact.InvalidationReason = reason;
                artifactIds.Add(artifact.ArtifactId);
                queue.Enqueue(("artifact", artifact.ArtifactId.ToString()));
            }
        }

        return new PropagationResult(artifactIds, memoryIds);
    }

    private static async Task<ResourceRemoval> RemoveResourceAsync(
        IRouteDbContext context,
        string tenantId,
        LifecycleResourceKind kind,
        Guid resourceId,
        CancellationToken cancellationToken)
    {
        var targetKind = KindName(kind);
        var targetReference = resourceId.ToString();
        var edges = await context.DependencyEdges
            .Where(item =>
                (item.SourceKind == targetKind && item.SourceId == resourceId) ||
                (item.TenantId == tenantId &&
                 item.TargetKind == targetKind &&
                 item.TargetReference == targetReference))
            .ToListAsync(cancellationToken);
        context.DependencyEdges.RemoveRange(edges);

        bool deleted;
        if (kind == LifecycleResourceKind.Artifact)
        {
            var resource = await context.Artifacts.SingleOrDefaultAsync(
                item => item.TenantId == tenantId && item.ArtifactId == resourceId,
                cancellationToken);
            deleted = resource is not null;
            if (resource is not null)
            {
                context.Artifacts.Remove(resource);
                var pointers = await context.Artifacts.Where(item =>
                        item.TenantId == tenantId &&
                        (item.SupersedesArtifactId == resourceId || item.SupersededByArtifactId == resourceId))
                    .ToListAsync(cancellationToken);
                foreach (var pointer in pointers)
                {
                    if (pointer.SupersedesArtifactId == resourceId)
                    {
                        pointer.SupersedesArtifactId = null;
                    }

                    if (pointer.SupersededByArtifactId == resourceId)
                    {
                        pointer.SupersededByArtifactId = null;
                    }
                }
            }
        }
        else
        {
            var resource = await context.MemoryRecords.SingleOrDefaultAsync(
                item => item.TenantId == tenantId && item.MemoryId == resourceId,
                cancellationToken);
            deleted = resource is not null;
            if (resource is not null)
            {
                context.MemoryRecords.Remove(resource);
                var pointers = await context.MemoryRecords.Where(item =>
                        item.TenantId == tenantId &&
                        (item.SupersedesMemoryId == resourceId || item.SupersededByMemoryId == resourceId))
                    .ToListAsync(cancellationToken);
                foreach (var pointer in pointers)
                {
                    if (pointer.SupersedesMemoryId == resourceId)
                    {
                        pointer.SupersedesMemoryId = null;
                    }

                    if (pointer.SupersededByMemoryId == resourceId)
                    {
                        pointer.SupersededByMemoryId = null;
                    }
                }
            }
        }

        return new ResourceRemoval(deleted, edges.Count);
    }

    private static async Task<int> PurgeArchivesAsync(
        IRouteDbContext context,
        LifecyclePolicy policy,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var archives = await context.LifecycleArchives
            .OrderBy(item => item.ArchivedAtUnixMilliseconds)
            .ThenBy(item => item.ResourceId)
            .ToArrayAsync(cancellationToken);
        var artifactIds = (await context.Artifacts.AsNoTracking()
                .Select(item => item.ArtifactId)
                .ToArrayAsync(cancellationToken))
            .ToHashSet();
        var memoryIds = (await context.MemoryRecords.AsNoTracking()
                .Select(item => item.MemoryId)
                .ToArrayAsync(cancellationToken))
            .ToHashSet();
        var purge = new HashSet<(LifecycleResourceKind Kind, Guid Id)>();
        var retentionCutoff = now.Subtract(policy.ArchiveRetention).ToUnixTimeMilliseconds();
        foreach (var archive in archives.Where(item =>
                     item.ArchivedAtUnixMilliseconds <= retentionCutoff &&
                     !Exists(item.ResourceKind, item.ResourceId)))
        {
            purge.Add((archive.ResourceKind, archive.ResourceId));
        }

        foreach (var tenant in archives.GroupBy(item => item.TenantId, StringComparer.Ordinal))
        {
            var overflow = tenant.Count() - policy.MaxArchivesPerTenant;
            if (overflow <= 0)
            {
                continue;
            }

            foreach (var archive in tenant.Where(item => !Exists(item.ResourceKind, item.ResourceId)).Take(overflow))
            {
                purge.Add((archive.ResourceKind, archive.ResourceId));
            }
        }

        context.LifecycleArchives.RemoveRange(archives.Where(item =>
            purge.Contains((item.ResourceKind, item.ResourceId))));
        return purge.Count;

        bool Exists(LifecycleResourceKind kind, Guid id) => kind switch
        {
            LifecycleResourceKind.Artifact => artifactIds.Contains(id),
            _ => memoryIds.Contains(id)
        };
    }

    private static async Task<string> SerializePayloadAsync(
        IRouteDbContext context,
        LifecycleCandidate candidate,
        CancellationToken cancellationToken)
    {
        var dependencies = await EfMemoryStore.ReadDependenciesAsync(
            context,
            KindName(candidate.Kind),
            candidate.ResourceId,
            cancellationToken);
        object? entity = candidate.Kind switch
        {
            LifecycleResourceKind.Artifact => await context.Artifacts.AsNoTracking().SingleOrDefaultAsync(
                item => item.ArtifactId == candidate.ResourceId,
                cancellationToken),
            _ => await context.MemoryRecords.AsNoTracking().SingleOrDefaultAsync(
                item => item.MemoryId == candidate.ResourceId,
                cancellationToken)
        };
        return JsonSerializer.Serialize(new { entity, dependencies }, JsonOptions);
    }

    private static async Task<bool> ResourceExistsAsync(
        IRouteDbContext context,
        string tenantId,
        LifecycleResourceKind kind,
        Guid resourceId,
        CancellationToken cancellationToken) => kind switch
    {
        LifecycleResourceKind.Artifact => await context.Artifacts.AsNoTracking().AnyAsync(
            item => item.TenantId == tenantId && item.ArtifactId == resourceId,
            cancellationToken),
        _ => await context.MemoryRecords.AsNoTracking().AnyAsync(
            item => item.TenantId == tenantId && item.MemoryId == resourceId,
            cancellationToken)
    };

    private static async Task<LifecycleStorageSnapshot> SnapshotAsync(
        IRouteDbContext context,
        CancellationToken cancellationToken)
    {
        var artifactIds = (await context.Artifacts.AsNoTracking()
                .Select(item => item.ArtifactId)
                .ToArrayAsync(cancellationToken))
            .ToHashSet();
        var memoryIds = (await context.MemoryRecords.AsNoTracking()
                .Select(item => item.MemoryId)
                .ToArrayAsync(cancellationToken))
            .ToHashSet();
        var edges = await context.DependencyEdges.AsNoTracking().ToArrayAsync(cancellationToken);
        var dangling = edges.Count(item =>
            Guid.TryParse(item.TargetReference, out var id) && item.TargetKind switch
            {
                "artifact" => !artifactIds.Contains(id),
                "memory" => !memoryIds.Contains(id),
                _ => false
            });
        return new LifecycleStorageSnapshot(
            artifactIds.Count,
            memoryIds.Count,
            await context.LifecycleArchives.CountAsync(cancellationToken),
            edges.Length,
            dangling);
    }

    private static LifecycleCandidate ToCandidate(ArtifactEntity artifact) => new(
        LifecycleResourceKind.Artifact,
        artifact.ArtifactId,
        artifact.TenantId,
        artifact.Version,
        DateTimeOffset.FromUnixTimeMilliseconds(artifact.CreatedAtUnixMilliseconds),
        artifact.ExpiresAtUnixMilliseconds is { } expiresAt
            ? DateTimeOffset.FromUnixTimeMilliseconds(expiresAt)
            : null,
        artifact.IsActive && artifact.LifecycleStatus == ArtifactLifecycleStatus.Active,
        artifact.ContentHash);

    private static LifecycleCandidate ToCandidate(MemoryEntity memory) => new(
        LifecycleResourceKind.Memory,
        memory.MemoryId,
        memory.TenantId,
        memory.Version,
        DateTimeOffset.FromUnixTimeMilliseconds(memory.CreatedAtUnixMilliseconds),
        memory.ExpiresAtUnixMilliseconds is { } expiresAt
            ? DateTimeOffset.FromUnixTimeMilliseconds(expiresAt)
            : null,
        memory.LifecycleStatus == MemoryLifecycleStatus.Active,
        memory.ContentHash);

    private static bool IsActive(ArtifactEntity artifact, DateTimeOffset now) =>
        artifact.IsActive &&
        artifact.LifecycleStatus == ArtifactLifecycleStatus.Active &&
        (artifact.ExpiresAtUnixMilliseconds is null ||
         artifact.ExpiresAtUnixMilliseconds > now.ToUnixTimeMilliseconds());

    private static bool IsActive(MemoryEntity memory, DateTimeOffset now) =>
        memory.LifecycleStatus == MemoryLifecycleStatus.Active &&
        (memory.ExpiresAtUnixMilliseconds is null ||
         memory.ExpiresAtUnixMilliseconds > now.ToUnixTimeMilliseconds());

    private static string KindName(LifecycleResourceKind kind) => kind switch
    {
        LifecycleResourceKind.Artifact => "artifact",
        _ => "memory"
    };

    private static string ArchiveKey(
        string tenantId,
        LifecycleResourceKind kind,
        Guid id) => $"{tenantId}:{kind}:{id}";

    private sealed record LifecycleCandidate(
        LifecycleResourceKind Kind,
        Guid ResourceId,
        string TenantId,
        int Version,
        DateTimeOffset CreatedAt,
        DateTimeOffset? ExpiresAt,
        bool Active,
        string ContentHash);

    private sealed record PropagationResult(HashSet<Guid> Artifacts, HashSet<Guid> Memories);
    private sealed record ResourceRemoval(bool Deleted, int RemovedEdges);
}
