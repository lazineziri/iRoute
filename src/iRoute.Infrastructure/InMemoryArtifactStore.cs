using System.Collections.Concurrent;
using iRoute.Core;

namespace iRoute.Infrastructure;

public sealed class InMemoryArtifactStore : IArtifactStore
{
    private readonly ConcurrentDictionary<Guid, ArtifactRecord> _artifacts = new();
    private readonly ConcurrentDictionary<string, Guid> _reuseIndex = new(StringComparer.Ordinal);

    public Task<ArtifactRecord?> FindReusableAsync(
        ArtifactReuseQuery query,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var key = CreateReuseKey(
            query.TenantId,
            query.ProjectId,
            query.TaskType,
            query.TaskDefinitionVersion,
            query.InputHash);
        if (!_reuseIndex.TryGetValue(key, out var artifactId) ||
            !_artifacts.TryGetValue(artifactId, out var artifact) ||
            !artifact.IsActive ||
            artifact.ExpiresAt is { } expiresAt && expiresAt <= query.At)
        {
            return Task.FromResult<ArtifactRecord?>(null);
        }

        return Task.FromResult<ArtifactRecord?>(artifact);
    }

    public Task<ArtifactRecord?> GetAsync(Guid artifactId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(_artifacts.GetValueOrDefault(artifactId));
    }

    public Task<ArtifactRecord> SaveAsync(ArtifactRecord artifact, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var key = CreateReuseKey(
            artifact.TenantId,
            artifact.ProjectId,
            artifact.TaskType,
            artifact.TaskDefinitionVersion,
            artifact.InputHash);
        if (_reuseIndex.TryGetValue(key, out var existingId) &&
            _artifacts.TryGetValue(existingId, out var existing) &&
            existing.IsActive)
        {
            return Task.FromResult(existing);
        }

        if (!_artifacts.TryAdd(artifact.ArtifactId, artifact))
        {
            throw new InvalidOperationException("Artifact already exists.");
        }

        if (!_reuseIndex.TryAdd(key, artifact.ArtifactId))
        {
            _artifacts.TryRemove(artifact.ArtifactId, out _);
            var winnerId = _reuseIndex[key];
            return Task.FromResult(_artifacts[winnerId]);
        }

        return Task.FromResult(artifact);
    }

    private static string CreateReuseKey(
        string tenantId,
        string? projectId,
        string taskType,
        int taskDefinitionVersion,
        string inputHash) =>
        string.Join('\u001f', tenantId, projectId ?? string.Empty, taskType, taskDefinitionVersion, inputHash);
}
