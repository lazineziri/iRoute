using System.Data;
using iRoute.Common;
using Microsoft.EntityFrameworkCore;

namespace iRoute.Data;

public sealed partial class EfLifecycleStore
{
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

}
