using System.Collections.Frozen;
using System.Text.Json;
using iRoute.Common;

namespace iRoute.Services;

public sealed partial class BoundedContextCompiler(
    IMemoryStore memories,
    IArtifactStore artifacts,
    TimeProvider clock) : IContextCompiler
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
    private static readonly FrozenSet<string> ContextSourceProperties = new[]
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
    }.ToFrozenSet(StringComparer.Ordinal);

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
                new ActiveMemoryQuery(RequestScope.Tenant(request), request.ProjectId, clock.GetUtcNow()),
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

}
