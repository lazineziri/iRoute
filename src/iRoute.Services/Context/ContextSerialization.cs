using System.Text.Json;
using System.Text.RegularExpressions;
using iRoute.Common;

namespace iRoute.Services;

public sealed partial class BoundedContextCompiler
{
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
        return expiresAt is not null && expiresAt <= clock.GetUtcNow()
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
