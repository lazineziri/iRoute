using System.Text.Json;
using System.Text.RegularExpressions;
using iRoute.Contracts;
using iRoute.Core;

namespace iRoute.Runtime;

public sealed partial class BoundedContextCompiler(
    IMemoryStore memories,
    IArtifactStore artifacts,
    IClock clock) : IContextCompiler
{
    private const int MaximumHistoryItems = 3;
    private const int MaximumArtifactSections = 4;
    private const int CurrentDecisionRank = 950;
    private const int CurrentFactRank = 925;
    private const int AuthoritativeSourceRank = 900;
    private const int ActiveDecisionRank = 850;
    private const int ActiveFactRank = 800;
    private const int ArtifactRank = 700;
    private const int PreferenceRank = 600;
    private const int ExplicitContextRank = 500;
    private const int RecentEventRank = 400;
    private const int SummaryRank = 350;
    private const int HistoryRank = 300;
    private const int StoredHistoryRank = 250;
    private static readonly HashSet<string> ContextSourceProperties = new(StringComparer.Ordinal)
    {
        "activeDecisions",
        "decisions",
        "facts",
        "authoritativeSources",
        "preferences",
        "context",
        "recentEvents",
        "summaries",
        "projectHistory",
        "contextArtifacts"
    };

    public async Task<CompiledContext> CompileAsync(
        TaskRequest request,
        TaskDefinition definition,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var budget = Math.Max(1, request.Constraints?.MaxInputTokens ?? definition.DefaultMaxInputTokens);
        var projectedInput = ProjectInput(request.Input);
        var projectedInputTokens = TokenEstimator.Estimate(projectedInput);
        var emptyContextTokens = TokenEstimator.Estimate(SerializeContext(
            new Dictionary<string, List<JsonElement>>(StringComparer.Ordinal)));
        if (projectedInputTokens + emptyContextTokens > budget)
        {
            throw new ContextCompilationException(
                ErrorCodes.ContextBudgetExceeded,
                "Context input budget exceeded",
                $"The projected task input requires {projectedInputTokens + emptyContextTokens} estimated tokens, above the task limit of {budget}.");
        }

        var candidates = new List<ContextCandidate>();
        var entries = new List<ContextManifestEntry>();
        ExtractRequestCandidates(request.Input, candidates, entries);

        if (!string.IsNullOrWhiteSpace(request.ProjectId))
        {
            var activeMemory = await memories.ListActiveAsync(
                new ActiveMemoryQuery(RequestScope.Tenant(request), request.ProjectId, clock.UtcNow),
                cancellationToken);
            candidates.AddRange(activeMemory.Select(CreateMemoryCandidate));
        }

        var artifactSliced = await ExtractArtifactCandidatesAsync(
            request,
            budget,
            candidates,
            entries,
            cancellationToken);
        var keywords = ExtractKeywords(request);
        var ranked = candidates
            .Select(candidate => candidate with { Relevance = CalculateRelevance(candidate.Value, keywords) })
            .OrderByDescending(candidate => candidate.Rank)
            .ThenByDescending(candidate => candidate.Relevance)
            .ThenByDescending(candidate => candidate.Version)
            .ThenByDescending(candidate => candidate.Order)
            .ThenBy(candidate => candidate.Reference, StringComparer.Ordinal)
            .ToArray();

        var superseded = new HashSet<ContextCandidate>();
        foreach (var group in ranked
                     .Where(candidate => candidate.Identity is not null)
                     .GroupBy(candidate => candidate.Identity!, StringComparer.Ordinal))
        {
            foreach (var candidate in group.Skip(1))
            {
                superseded.Add(candidate);
                entries.Add(ToEntry(
                    candidate,
                    false,
                    "Excluded because a higher-ranked active source superseded this candidate."));
            }
        }

        var distinct = new List<ContextCandidate>();
        var includedHashes = new Dictionary<string, ContextCandidate>(StringComparer.Ordinal);
        foreach (var candidate in ranked.Where(candidate => !superseded.Contains(candidate)))
        {
            if (includedHashes.TryGetValue(candidate.ContentHash, out var duplicateOf))
            {
                entries.Add(ToEntry(
                    candidate,
                    false,
                    $"Excluded as an exact duplicate of '{duplicateOf.Reference}'."));
                continue;
            }

            includedHashes[candidate.ContentHash] = candidate;
            distinct.Add(candidate);
        }

        var history = distinct.Where(candidate => candidate.Kind == "history").ToArray();
        var historyExcluded = history.Skip(MaximumHistoryItems).ToHashSet();
        foreach (var candidate in historyExcluded)
        {
            entries.Add(ToEntry(
                candidate,
                false,
                $"Excluded because full history is disabled; at most {MaximumHistoryItems} recent relevant items are eligible."));
        }

        var selected = new Dictionary<string, List<JsonElement>>(StringComparer.Ordinal);
        var provenance = new SortedDictionary<string, EvidenceReference>(StringComparer.Ordinal);
        var truncated = artifactSliced || historyExcluded.Count > 0;
        var content = SerializeContext(selected);
        foreach (var candidate in distinct.Where(candidate => !historyExcluded.Contains(candidate)))
        {
            if (!selected.TryGetValue(candidate.Bucket, out var bucket))
            {
                bucket = [];
                selected[candidate.Bucket] = bucket;
            }

            var index = bucket.Count;
            bucket.Add(candidate.Value.Clone());
            var proposed = SerializeContext(selected);
            var proposedTokens = projectedInputTokens + TokenEstimator.Estimate(proposed);
            if (proposedTokens > budget)
            {
                bucket.RemoveAt(bucket.Count - 1);
                if (bucket.Count == 0)
                {
                    selected.Remove(candidate.Bucket);
                }

                truncated = true;
                entries.Add(ToEntry(
                    candidate,
                    false,
                    "Excluded because the fully serialized context package would exceed the task token budget."));
                continue;
            }

            content = proposed;
            var outputPath = $"/{EscapeJsonPointer(candidate.Bucket)}/{index}";
            provenance[outputPath] = new EvidenceReference(
                candidate.SourceKind,
                candidate.Reference,
                candidate.ContentHash,
                candidate.ObservedAt);
            entries.Add(ToEntry(
                candidate,
                true,
                candidate.WasSliced
                    ? "Included as a bounded artifact section after ranking and safety checks."
                    : "Included after ranking, scope, freshness, supersession, deduplication, and budget checks.",
                outputPath));
        }

        var contextTokens = TokenEstimator.Estimate(content);
        var estimatedTokens = projectedInputTokens + contextTokens;
        var orderedEntries = entries
            .OrderByDescending(entry => entry.Included)
            .ThenByDescending(entry => entry.Rank)
            .ThenBy(entry => entry.Reference, StringComparer.Ordinal)
            .ToArray();
        var manifest = new ContextManifest(
            estimatedTokens,
            budget,
            projectedInputTokens,
            contextTokens,
            truncated,
            false,
            orderedEntries,
            provenance);
        var evidence = provenance.Values
            .DistinctBy(item => (item.Kind, item.Reference, item.ContentHash))
            .ToArray();
        return new CompiledContext(content, manifest, evidence, projectedInput);
    }

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
        (artifact.ExpiresAt is null || artifact.ExpiresAt > clock.UtcNow);

    private static IEnumerable<(string? Name, JsonElement Value)> SelectArtifactSections(
        ArtifactRecord artifact,
        string[] requestedSections,
        TaskRequest request)
    {
        if (artifact.Content.ValueKind != JsonValueKind.Object)
        {
            yield return (null, artifact.Content);
            yield break;
        }

        var properties = artifact.Content.EnumerateObject().ToArray();
        if (requestedSections.Length > 0)
        {
            foreach (var section in requestedSections)
            {
                var property = properties.FirstOrDefault(item =>
                    string.Equals(item.Name, section, StringComparison.Ordinal));
                if (property.Name is not null)
                {
                    yield return (property.Name, property.Value);
                }
            }

            yield break;
        }

        var keywords = ExtractKeywords(request);
        foreach (var property in properties
                     .OrderByDescending(item => CalculateRelevance(item.Value, keywords))
                     .ThenBy(item => item.Name, StringComparer.Ordinal))
        {
            yield return (property.Name, property.Value);
        }
    }

    private static JsonElement SliceValue(JsonElement value, int tokenLimit, out bool sliced)
    {
        if (TokenEstimator.Estimate(value) <= tokenLimit)
        {
            sliced = false;
            return value.Clone();
        }

        sliced = true;
        if (value.ValueKind == JsonValueKind.String)
        {
            var text = value.GetString() ?? string.Empty;
            var maximumCharacters = Math.Max(16, (tokenLimit * 4) - 3);
            return JsonSerializer.SerializeToElement(text[..Math.Min(text.Length, maximumCharacters)] + "...");
        }

        if (value.ValueKind == JsonValueKind.Array)
        {
            var selected = new List<JsonElement>();
            foreach (var item in value.EnumerateArray().Take(8))
            {
                var projected = SliceValue(item, Math.Max(8, tokenLimit / 2), out _);
                selected.Add(projected);
                if (TokenEstimator.Estimate(JsonSerializer.SerializeToElement(selected)) > tokenLimit)
                {
                    selected.RemoveAt(selected.Count - 1);
                    break;
                }
            }

            return JsonSerializer.SerializeToElement(selected);
        }

        if (value.ValueKind == JsonValueKind.Object)
        {
            var selected = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
            foreach (var property in value.EnumerateObject().Take(8))
            {
                var projected = SliceValue(property.Value, Math.Max(8, tokenLimit / 2), out _);
                selected[property.Name] = projected;
                if (TokenEstimator.Estimate(JsonSerializer.SerializeToElement(selected)) > tokenLimit)
                {
                    selected.Remove(property.Name);
                }
            }

            return JsonSerializer.SerializeToElement(selected);
        }

        return value.Clone();
    }

    private static JsonElement SerializeContext(Dictionary<string, List<JsonElement>> selected) =>
        JsonSerializer.SerializeToElement(selected);

    private static JsonElement ProjectInput(JsonElement input)
    {
        if (input.ValueKind != JsonValueKind.Object)
        {
            return input.Clone();
        }

        var projected = input.EnumerateObject()
            .Where(property => !ContextSourceProperties.Contains(property.Name))
            .ToDictionary(
                property => property.Name,
                property => property.Value.Clone(),
                StringComparer.Ordinal);
        return JsonSerializer.SerializeToElement(projected);
    }

    private static ContextManifestEntry ToEntry(
        ContextCandidate candidate,
        bool included,
        string reason,
        string? outputPath = null) => new(
        candidate.Kind,
        candidate.Reference,
        included,
        reason,
        TokenEstimator.Estimate(candidate.Value),
        candidate.ContentHash,
        candidate.Rank,
        outputPath);

    private string? InactiveReason(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        if (value.TryGetProperty("isActive", out var isActive) &&
            isActive.ValueKind == JsonValueKind.False)
        {
            return "Excluded because the source is not active.";
        }

        var lifecycle = ReadString(value, "lifecycleStatus") ?? ReadString(value, "status");
        if (lifecycle is not null &&
            (string.Equals(lifecycle, "Superseded", StringComparison.OrdinalIgnoreCase) ||
             string.Equals(lifecycle, "Invalidated", StringComparison.OrdinalIgnoreCase) ||
             string.Equals(lifecycle, "Expired", StringComparison.OrdinalIgnoreCase)))
        {
            return $"Excluded because the source lifecycle is {lifecycle}.";
        }

        if (ReadString(value, "supersededBy") is not null ||
            ReadString(value, "supersededByMemoryId") is not null ||
            ReadString(value, "supersededByArtifactId") is not null)
        {
            return "Excluded because the source has been superseded.";
        }

        var expiresAt = ReadDateTimeOffset(value, "expiresAt");
        return expiresAt is not null && expiresAt <= clock.UtcNow
            ? "Excluded because the source is expired."
            : null;
    }

    private static JsonElement ReadPayload(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object)
        {
            return value;
        }

        foreach (var propertyName in new[] { "content", "value", "text", "summary" })
        {
            if (value.TryGetProperty(propertyName, out var payload))
            {
                return payload;
            }
        }

        return value;
    }

    private static string? ReadString(JsonElement value, string propertyName) =>
        value.ValueKind == JsonValueKind.Object &&
        value.TryGetProperty(propertyName, out var property) &&
        property.ValueKind == JsonValueKind.String &&
        !string.IsNullOrWhiteSpace(property.GetString())
            ? property.GetString()!.Trim()
            : null;

    private static int ReadInt32(JsonElement value, string propertyName) =>
        value.ValueKind == JsonValueKind.Object &&
        value.TryGetProperty(propertyName, out var property) &&
        property.TryGetInt32(out var number)
            ? number
            : 0;

    private static DateTimeOffset? ReadDateTimeOffset(JsonElement value, string propertyName) =>
        ReadString(value, propertyName) is { } text && DateTimeOffset.TryParse(text, out var result)
            ? result
            : null;

    private static string[] ReadStringArray(JsonElement value, string propertyName) =>
        value.ValueKind == JsonValueKind.Object &&
        value.TryGetProperty(propertyName, out var property) &&
        property.ValueKind == JsonValueKind.Array
            ? property.EnumerateArray()
                .Where(item => item.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(item.GetString()))
                .Select(item => item.GetString()!.Trim())
                .Distinct(StringComparer.Ordinal)
                .ToArray()
            : [];

    private static HashSet<string> ExtractKeywords(TaskRequest request)
    {
        var text = request.TaskType + " " + string.Join(
            " ",
            request.Input.ValueKind == JsonValueKind.Object
                ? request.Input.EnumerateObject()
                    .Where(property => property.Name is not
                        ("projectHistory" or "contextArtifacts" or "authoritativeSources" or "context"))
                    .Select(property => property.Value.GetRawText())
                : [request.Input.GetRawText()]);
        return WordPattern()
            .Matches(text.ToLowerInvariant())
            .Select(match => match.Value)
            .Where(word => word.Length >= 3)
            .ToHashSet(StringComparer.Ordinal);
    }

    private static int CalculateRelevance(JsonElement value, IReadOnlySet<string> keywords) =>
        WordPattern()
            .Matches(value.GetRawText().ToLowerInvariant())
            .Select(match => match.Value)
            .Distinct(StringComparer.Ordinal)
            .Count(keywords.Contains);

    private static string EscapeJsonPointer(string value) =>
        value.Replace("~", "~0", StringComparison.Ordinal).Replace("/", "~1", StringComparison.Ordinal);

    [GeneratedRegex("[a-z0-9]+", RegexOptions.CultureInvariant)]
    private static partial Regex WordPattern();

    private sealed record ContextCandidate(
        string Bucket,
        string Kind,
        string SourceKind,
        string Reference,
        JsonElement Value,
        string ContentHash,
        int Rank,
        int Version,
        int Order,
        string? Identity,
        DateTimeOffset? ObservedAt,
        bool WasSliced = false,
        int Relevance = 0);
}

internal sealed class ContextCompilationException(
    string code,
    string title,
    string detail) : Exception(detail)
{
    public string Code { get; } = code;
    public string Title { get; } = title;
}
