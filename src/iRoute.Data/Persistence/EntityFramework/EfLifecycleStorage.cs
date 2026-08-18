using System.Data;
using System.Text.Json;
using iRoute.Common;
using Microsoft.EntityFrameworkCore;

namespace iRoute.Data;

public sealed partial class EfLifecycleStore
{
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
