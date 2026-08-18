using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using iRoute.Contracts;
using iRoute.Core;

namespace iRoute.Runtime;

public sealed record ProjectMemoryMaterialization(
    MemoryWriteResult Write,
    MemoryInvalidationResult InvalidatedMemory,
    ArtifactInvalidationResult InvalidatedArtifacts);

public sealed class ProjectMemoryMaterializer(
    IMemoryStore memories,
    IArtifactStore artifacts,
    IClock clock)
{
    public async Task<IReadOnlyList<ProjectMemoryMaterialization>> MaterializeAsync(
        TaskRequest request,
        Guid executionId,
        CancellationToken cancellationToken)
    {
        var tenantId = RequestScope.Tenant(request);
        var materialized = new List<ProjectMemoryMaterialization>();
        foreach (var candidate in ExtractCandidates(request.Input))
        {
            var now = clock.UtcNow;
            var contentHash = CanonicalJson.Hash(candidate.Value);
            var evidence = new EvidenceReference(
                "request.input",
                executionId.ToString(),
                contentHash,
                now);
            var write = await memories.UpsertAsync(
                new MemoryRecord(
                    Guid.CreateVersion7(),
                    tenantId,
                    request.ProjectId,
                    candidate.Kind,
                    candidate.Key,
                    1,
                    candidate.Value.Clone(),
                    contentHash,
                    MemoryLifecycleStatus.Active,
                    [evidence],
                    [new DependencyReference("request.input", $"{request.TaskType}:{candidate.Key}", contentHash)],
                    now),
                cancellationToken);

            var invalidatedMemory = new MemoryInvalidationResult([]);
            var invalidatedArtifacts = new ArtifactInvalidationResult([]);
            if (write.Created && write.Previous is not null)
            {
                var change = new DependencyChange(
                    tenantId,
                    "memory",
                    write.Previous.MemoryId.ToString(),
                    null,
                    true,
                    $"Memory '{candidate.Key}' was superseded by version {write.Record.Version}.",
                    now);
                invalidatedMemory = await memories.InvalidateByDependencyAsync(change, cancellationToken);
                invalidatedArtifacts = await artifacts.InvalidateByDependencyAsync(change, cancellationToken);
                if (invalidatedMemory.MemoryIds.Count > 0)
                {
                    var invalidatedArtifactIds = new HashSet<Guid>(invalidatedArtifacts.ArtifactIds);
                    foreach (var memoryId in invalidatedMemory.MemoryIds)
                    {
                        var dependentInvalidation = await artifacts.InvalidateByDependencyAsync(
                            change with { Reference = memoryId.ToString() },
                            cancellationToken);
                        invalidatedArtifactIds.UnionWith(dependentInvalidation.ArtifactIds);
                    }

                    invalidatedArtifacts = new ArtifactInvalidationResult(
                        invalidatedArtifactIds.Order().ToArray());
                }
            }

            materialized.Add(new ProjectMemoryMaterialization(
                write,
                invalidatedMemory,
                invalidatedArtifacts));
        }

        return materialized;
    }

    private static IEnumerable<MemoryCandidate> ExtractCandidates(JsonElement input)
    {
        if (input.ValueKind != JsonValueKind.Object)
        {
            yield break;
        }

        foreach (var property in input.EnumerateObject())
        {
            var kind = property.Name switch
            {
                "activeDecisions" or "decisions" => MemoryKind.Decision,
                "facts" or "projectHistory" => MemoryKind.Fact,
                _ => (MemoryKind?)null
            };
            if (kind is null)
            {
                continue;
            }

            foreach (var candidate in Expand(property.Name, kind.Value, property.Value))
            {
                yield return candidate;
            }
        }
    }

    private static IEnumerable<MemoryCandidate> Expand(
        string sourceName,
        MemoryKind kind,
        JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Array)
        {
            var index = 0;
            foreach (var item in value.EnumerateArray())
            {
                yield return CreateCandidate(
                    kind,
                    ReadExplicitKey(item) ?? $"{sourceName}[{index}]",
                    item);
                index++;
            }

            yield break;
        }

        if (value.ValueKind == JsonValueKind.Object && ReadExplicitKey(value) is null)
        {
            foreach (var property in value.EnumerateObject())
            {
                yield return CreateCandidate(
                    kind,
                    $"{sourceName}.{property.Name}",
                    property.Value);
            }

            yield break;
        }

        yield return CreateCandidate(
            kind,
            ReadExplicitKey(value) ?? sourceName,
            value);
    }

    private static string? ReadExplicitKey(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        foreach (var propertyName in new[] { "key", "id" })
        {
            if (value.TryGetProperty(propertyName, out var property) &&
                property.ValueKind == JsonValueKind.String &&
                !string.IsNullOrWhiteSpace(property.GetString()))
            {
                return property.GetString()!.Trim();
            }
        }

        return null;
    }

    private static MemoryCandidate CreateCandidate(MemoryKind kind, string key, JsonElement value) =>
        new(kind, NormalizeKey(key), value.Clone());

    private static string NormalizeKey(string key)
    {
        var normalized = key.Trim();
        if (normalized.Length <= 200)
        {
            return normalized;
        }

        var suffix = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalized)))
            .ToLowerInvariant();
        return $"{normalized[..135]}:{suffix}";
    }

    private sealed record MemoryCandidate(MemoryKind Kind, string Key, JsonElement Value);
}
