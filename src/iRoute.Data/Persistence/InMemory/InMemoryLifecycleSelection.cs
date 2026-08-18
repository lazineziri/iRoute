using iRoute.Common;

namespace iRoute.Data;

public sealed partial class InMemoryLifecycleStore
{
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

}
