using System.Data;
using System.Text.Json;
using iRoute.Common;
using Microsoft.EntityFrameworkCore;

namespace iRoute.Data;

public sealed partial class EfLifecycleStore(IDbContextFactory<IRouteDbContext> contextFactory) : ILifecycleStore
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

}
