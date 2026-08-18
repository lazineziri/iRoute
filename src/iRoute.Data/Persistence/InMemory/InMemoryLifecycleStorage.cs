using System.Text.Json;
using iRoute.Common;

namespace iRoute.Data;

public sealed partial class InMemoryLifecycleStore
{
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
