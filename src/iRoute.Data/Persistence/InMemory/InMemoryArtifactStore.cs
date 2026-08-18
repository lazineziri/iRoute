using System.Collections.Concurrent;
using iRoute.Common;

namespace iRoute.Data;

public sealed class InMemoryArtifactStore(LifecyclePolicy? lifecyclePolicy = null) : IArtifactStore
{
    private readonly ConcurrentDictionary<Guid, ArtifactRecord> _artifacts = new();
    private readonly object _sync = new();
    private readonly LifecyclePolicy _lifecyclePolicy = lifecyclePolicy ?? new LifecyclePolicy();

    public Task<ArtifactRecord?> FindReusableAsync(
        ArtifactReuseQuery query,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_sync)
        {
            var artifact = _artifacts.Values
                .Where(item =>
                    string.Equals(item.TenantId, query.TenantId, StringComparison.Ordinal) &&
                    string.Equals(item.ProjectId ?? string.Empty, query.ProjectId ?? string.Empty, StringComparison.Ordinal) &&
                    string.Equals(item.TaskType, query.TaskType, StringComparison.Ordinal) &&
                    item.TaskDefinitionVersion == query.TaskDefinitionVersion &&
                    string.Equals(item.InputHash, query.InputHash, StringComparison.Ordinal) &&
                    string.Equals(item.EffectiveLogicalKey, query.LogicalKey, StringComparison.Ordinal) &&
                    item.IsActive &&
                    item.LifecycleStatus == ArtifactLifecycleStatus.Active &&
                    (item.ExpiresAt is null || item.ExpiresAt > query.At))
                .OrderByDescending(item => item.Version)
                .ThenByDescending(item => item.ArtifactId)
                .FirstOrDefault();
            return Task.FromResult<ArtifactRecord?>(artifact);
        }
    }

    public Task<ArtifactRecord?> GetAsync(
        string tenantId,
        Guid artifactId,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_sync)
        {
            var artifact = _artifacts.GetValueOrDefault(artifactId);
            return Task.FromResult<ArtifactRecord?>(
                artifact is not null && string.Equals(artifact.TenantId, tenantId, StringComparison.Ordinal)
                    ? artifact
                    : null);
        }
    }

    public Task<ArtifactRecord?> FindActiveAsync(
        ArtifactLookupQuery query,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_sync)
        {
            var artifact = _artifacts.Values
                .Where(item =>
                    string.Equals(item.TenantId, query.TenantId, StringComparison.Ordinal) &&
                    string.Equals(item.ProjectId ?? string.Empty, query.ProjectId ?? string.Empty, StringComparison.Ordinal) &&
                    string.Equals(item.TaskType, query.TaskType, StringComparison.Ordinal) &&
                    item.TaskDefinitionVersion == query.TaskDefinitionVersion &&
                    string.Equals(item.ArtifactType, query.ArtifactType, StringComparison.Ordinal) &&
                    string.Equals(item.EffectiveLogicalKey, query.LogicalKey, StringComparison.Ordinal) &&
                    item.IsActive &&
                    item.LifecycleStatus == ArtifactLifecycleStatus.Active &&
                    (item.ExpiresAt is null || item.ExpiresAt > query.At))
                .OrderByDescending(item => item.Version)
                .ThenByDescending(item => item.ArtifactId)
                .FirstOrDefault();
            return Task.FromResult<ArtifactRecord?>(artifact);
        }
    }

    public Task<ArtifactRecord> SaveAsync(ArtifactRecord artifact, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_sync)
        {
            if (_artifacts.ContainsKey(artifact.ArtifactId))
            {
                throw new InvalidOperationException("Artifact already exists.");
            }

            var logicalKey = artifact.EffectiveLogicalKey;
            var lineage = _artifacts.Values
                .Where(item => SameLineage(item, artifact, logicalKey))
                .OrderByDescending(item => item.Version)
                .ThenByDescending(item => item.ArtifactId)
                .ToArray();
            var active = lineage.FirstOrDefault(item =>
                item.IsActive && item.LifecycleStatus == ArtifactLifecycleStatus.Active);
            if (active is not null &&
                string.Equals(active.InputHash, artifact.InputHash, StringComparison.Ordinal) &&
                string.Equals(active.ContentHash, artifact.ContentHash, StringComparison.Ordinal) &&
                (active.ExpiresAt is null || active.ExpiresAt > artifact.CreatedAt))
            {
                return Task.FromResult(active);
            }

            var previous = lineage.FirstOrDefault();
            var versioned = artifact with
            {
                Version = checked((previous?.Version ?? 0) + 1),
                LogicalKey = logicalKey,
                IsActive = true,
                LifecycleStatus = ArtifactLifecycleStatus.Active,
                SupersedesArtifactId = previous?.ArtifactId,
                SupersededByArtifactId = null,
                Dependencies = artifact.EffectiveDependencies
                    .DistinctBy(item => (item.Kind, item.Reference))
                    .OrderBy(item => item.Kind, StringComparer.Ordinal)
                    .ThenBy(item => item.Reference, StringComparer.Ordinal)
                    .ToArray(),
                InvalidatedAt = null,
                InvalidationReason = null,
                ExpiresAt = artifact.ExpiresAt ??
                    artifact.CreatedAt.Add(_lifecyclePolicy.DefaultArtifactTimeToLive),
                Content = artifact.Content.Clone()
            };
            if (active is not null)
            {
                _artifacts[active.ArtifactId] = active with
                {
                    IsActive = false,
                    LifecycleStatus = ArtifactLifecycleStatus.Superseded,
                    SupersededByArtifactId = versioned.ArtifactId
                };
            }

            _artifacts[versioned.ArtifactId] = versioned;
            return Task.FromResult(versioned);
        }
    }

    internal IReadOnlyList<ArtifactRecord> LifecycleSnapshot()
    {
        lock (_sync)
        {
            return _artifacts.Values.ToArray();
        }
    }

    internal bool LifecycleUpdate(ArtifactRecord artifact)
    {
        lock (_sync)
        {
            if (!_artifacts.ContainsKey(artifact.ArtifactId))
            {
                return false;
            }

            _artifacts[artifact.ArtifactId] = artifact;
            return true;
        }
    }

    internal bool LifecycleRemove(Guid artifactId)
    {
        lock (_sync)
        {
            return _artifacts.TryRemove(artifactId, out _);
        }
    }

    public Task<ArtifactInvalidationResult> InvalidateByDependencyAsync(
        DependencyChange change,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_sync)
        {
            var invalidated = new List<Guid>();
            var changes = new Queue<(string Kind, string Reference, string? Hash, bool Removed)>();
            changes.Enqueue((change.Kind, change.Reference, change.CurrentContentHash, change.SourceRemoved));
            while (changes.TryDequeue(out var dependencyChange))
            {
                var candidates = _artifacts.Values
                    .Where(item =>
                        string.Equals(item.TenantId, change.TenantId, StringComparison.Ordinal) &&
                        item.IsActive &&
                        item.LifecycleStatus == ArtifactLifecycleStatus.Active &&
                        item.EffectiveDependencies.Any(dependency =>
                            Matches(dependency, dependencyChange)))
                    .OrderBy(item => item.Version)
                    .ThenBy(item => item.ArtifactId)
                    .ToArray();
                foreach (var candidate in candidates)
                {
                    var updated = candidate with
                    {
                        IsActive = false,
                        LifecycleStatus = ArtifactLifecycleStatus.Invalidated,
                        InvalidatedAt = change.OccurredAt,
                        InvalidationReason = change.Reason
                    };
                    _artifacts[candidate.ArtifactId] = updated;
                    invalidated.Add(candidate.ArtifactId);
                    changes.Enqueue((
                        "artifact",
                        candidate.ArtifactId.ToString(),
                        candidate.ContentHash,
                        true));
                }
            }

            return Task.FromResult(new ArtifactInvalidationResult(invalidated));
        }
    }

    private static bool SameLineage(ArtifactRecord existing, ArtifactRecord proposed, string logicalKey) =>
        string.Equals(existing.TenantId, proposed.TenantId, StringComparison.Ordinal) &&
        string.Equals(existing.ProjectId ?? string.Empty, proposed.ProjectId ?? string.Empty, StringComparison.Ordinal) &&
        string.Equals(existing.ArtifactType, proposed.ArtifactType, StringComparison.Ordinal) &&
        string.Equals(existing.EffectiveLogicalKey, logicalKey, StringComparison.Ordinal);

    private static bool Matches(
        DependencyReference dependency,
        (string Kind, string Reference, string? Hash, bool Removed) change) =>
        string.Equals(dependency.Kind, change.Kind, StringComparison.Ordinal) &&
        string.Equals(dependency.Reference, change.Reference, StringComparison.Ordinal) &&
        (change.Removed ||
            change.Hash is null ||
            !string.Equals(dependency.ContentHash, change.Hash, StringComparison.Ordinal));
}
