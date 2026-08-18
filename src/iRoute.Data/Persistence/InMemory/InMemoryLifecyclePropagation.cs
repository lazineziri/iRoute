using iRoute.Common;

namespace iRoute.Data;

public sealed partial class InMemoryLifecycleStore
{
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

}
