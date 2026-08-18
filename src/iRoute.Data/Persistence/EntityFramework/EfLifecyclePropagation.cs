using System.Data;
using iRoute.Common;
using Microsoft.EntityFrameworkCore;

namespace iRoute.Data;

public sealed partial class EfLifecycleStore
{
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

}
