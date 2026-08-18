using System.Text.Json;
using iRoute.Common;

namespace iRoute.Services;

public sealed partial class BoundedContextCompiler
{
    private void ExtractRequestCandidates(
        JsonElement input,
        List<ContextCandidate> candidates,
        List<ContextManifestEntry> entries)
    {
        if (input.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        AddRequestSource(input, "activeDecisions", "activeDecisions", "decision", CurrentDecisionRank, candidates, entries);
        AddRequestSource(input, "decisions", "activeDecisions", "decision", CurrentDecisionRank, candidates, entries);
        AddRequestSource(input, "facts", "facts", "fact", CurrentFactRank, candidates, entries);
        AddRequestSource(input, "authoritativeSources", "authoritativeSources", "authoritative-source", AuthoritativeSourceRank, candidates, entries);
        AddRequestSource(input, "preferences", "preferences", "preference", PreferenceRank, candidates, entries);
        AddRequestSource(input, "context", "context", "context", ExplicitContextRank, candidates, entries);
        AddRequestSource(input, "recentEvents", "recentEvents", "recent-event", RecentEventRank, candidates, entries);
        AddRequestSource(input, "summaries", "summaries", "summary", SummaryRank, candidates, entries);
        AddRequestSource(input, "projectHistory", "projectHistory", "history", HistoryRank, candidates, entries);
    }

    private void AddRequestSource(
        JsonElement input,
        string propertyName,
        string bucket,
        string kind,
        int rank,
        List<ContextCandidate> candidates,
        List<ContextManifestEntry> entries)
    {
        if (!input.TryGetProperty(propertyName, out var source))
        {
            return;
        }

        var expanded = source.ValueKind == JsonValueKind.Array
            ? source.EnumerateArray().Select((value, index) => (Value: value, Index: index)).ToArray()
            : [(source, 0)];
        foreach (var item in expanded)
        {
            var defaultReference = source.ValueKind == JsonValueKind.Array
                ? $"input.{propertyName}[{item.Index}]"
                : $"input.{propertyName}";
            var reference = ReadString(item.Value, "reference") ?? defaultReference;
            var value = ReadPayload(item.Value);
            var contentHash = CanonicalJson.Hash(value);
            if (InactiveReason(item.Value) is { } inactiveReason)
            {
                entries.Add(new ContextManifestEntry(
                    kind,
                    reference,
                    false,
                    inactiveReason,
                    TokenEstimator.Estimate(value),
                    contentHash,
                    rank));
                continue;
            }

            var key = ReadString(item.Value, "key") ??
                (kind is "decision" or "fact" or "history"
                    ? $"{propertyName}[{item.Index}]"
                    : null);
            var identityKind = kind == "history" ? "fact" : kind;
            var identity = key is null ? null : $"{identityKind}:{key}";
            candidates.Add(new ContextCandidate(
                bucket,
                kind,
                "request",
                reference,
                value.Clone(),
                contentHash,
                rank,
                ReadInt32(item.Value, "version"),
                item.Index,
                identity,
                ReadDateTimeOffset(item.Value, "observedAt")));
        }
    }

    private ContextCandidate CreateMemoryCandidate(MemoryRecord memory)
    {
        var isHistory = memory.Kind == MemoryKind.Fact &&
            memory.Key.StartsWith("projectHistory[", StringComparison.Ordinal);
        var kind = isHistory
            ? "history"
            : memory.Kind == MemoryKind.Decision ? "decision" : "fact";
        return new ContextCandidate(
            isHistory ? "projectHistory" : memory.Kind == MemoryKind.Decision ? "activeDecisions" : "facts",
            kind,
            "memory",
            memory.MemoryId.ToString(),
            memory.Value.Clone(),
            memory.ContentHash,
            isHistory ? StoredHistoryRank : memory.Kind == MemoryKind.Decision ? ActiveDecisionRank : ActiveFactRank,
            memory.Version,
            memory.Version,
            $"{(kind == "history" ? "fact" : kind)}:{memory.Key}",
            memory.CreatedAt);
    }

    private async Task<bool> ExtractArtifactCandidatesAsync(
        TaskRequest request,
        int budget,
        List<ContextCandidate> candidates,
        List<ContextManifestEntry> entries,
        CancellationToken cancellationToken)
    {
        if (request.Input.ValueKind != JsonValueKind.Object ||
            !request.Input.TryGetProperty("contextArtifacts", out var requestedArtifacts))
        {
            return false;
        }

        var references = requestedArtifacts.ValueKind == JsonValueKind.Array
            ? requestedArtifacts.EnumerateArray().ToArray()
            : [requestedArtifacts];
        var slicedAny = false;
        for (var index = 0; index < references.Length; index++)
        {
            var requested = references[index];
            var artifactIdValue = requested.ValueKind == JsonValueKind.String
                ? requested.GetString()
                : ReadString(requested, "artifactId") ?? ReadString(requested, "id");
            var requestReference = $"input.contextArtifacts[{index}]";
            if (!Guid.TryParse(artifactIdValue, out var artifactId))
            {
                entries.Add(new ContextManifestEntry(
                    "artifact",
                    requestReference,
                    false,
                    "Rejected because contextArtifacts requires a valid artifactId.",
                    0,
                    Rank: ArtifactRank));
                continue;
            }

            var artifact = await artifacts.GetAsync(RequestScope.Tenant(request), artifactId, cancellationToken);
            if (!IsEligibleArtifact(artifact, request.ProjectId))
            {
                entries.Add(new ContextManifestEntry(
                    "artifact",
                    $"artifact:{artifactId}",
                    false,
                    "Rejected because the artifact did not match tenant/project scope, active lifecycle, or freshness requirements.",
                    0,
                    artifact?.ContentHash,
                    ArtifactRank));
                continue;
            }

            var requestedSections = ReadStringArray(requested, "sections");
            var sections = SelectArtifactSections(artifact!, requestedSections, request)
                .Take(MaximumArtifactSections)
                .ToArray();
            if (sections.Length == 0)
            {
                entries.Add(new ContextManifestEntry(
                    "artifact",
                    $"artifact:{artifactId}",
                    false,
                    "Rejected because none of the requested artifact sections exist.",
                    0,
                    artifact!.ContentHash,
                    ArtifactRank));
                continue;
            }

            var sliceBudget = Math.Clamp(budget / Math.Max(2, sections.Length), 16, 256);
            foreach (var section in sections)
            {
                var projected = SliceValue(section.Value, sliceBudget, out var wasSliced);
                slicedAny |= wasSliced;
                var pointer = section.Name is null ? string.Empty : $"/{EscapeJsonPointer(section.Name)}";
                var reference = $"artifact:{artifactId}#{pointer}";
                candidates.Add(new ContextCandidate(
                    "artifacts",
                    "artifact",
                    "artifact",
                    reference,
                    projected,
                    CanonicalJson.Hash(projected),
                    ArtifactRank,
                    artifact!.Version,
                    index,
                    $"artifact:{artifactId}:{section.Name ?? "$"}",
                    artifact.CreatedAt,
                    wasSliced));
            }
        }

        return slicedAny;
    }

    private bool IsEligibleArtifact(ArtifactRecord? artifact, string? projectId) =>
        artifact is not null &&
        string.Equals(artifact.ProjectId ?? string.Empty, projectId ?? string.Empty, StringComparison.Ordinal) &&
        artifact.IsActive &&
        artifact.LifecycleStatus == ArtifactLifecycleStatus.Active &&
        (artifact.ExpiresAt is null || artifact.ExpiresAt > clock.GetUtcNow());

}
