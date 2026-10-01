using System.Globalization;
using System.Text.Json;

namespace iRoute.Services;

internal static class ContextSourceLifecycle
{
    public static int Version(JsonElement value) =>
        value.ValueKind == JsonValueKind.Object && value.TryGetProperty("version", out var property) &&
        property.ValueKind == JsonValueKind.Number && property.TryGetInt32(out var version) && version > 0
            ? version : 0;

    public static string? ExclusionReason(JsonElement value, DateTimeOffset now)
    {
        if (value.ValueKind != JsonValueKind.Object) return null;
        if (value.TryGetProperty("isActive", out var isActive) && isActive.ValueKind == JsonValueKind.False)
            return "Excluded because the source is not active.";

        var lifecycle = ReadString(value, "lifecycleStatus") ?? ReadString(value, "status");
        if (lifecycle is not null &&
            (string.Equals(lifecycle, "Superseded", StringComparison.OrdinalIgnoreCase) ||
             string.Equals(lifecycle, "Invalidated", StringComparison.OrdinalIgnoreCase) ||
             string.Equals(lifecycle, "Expired", StringComparison.OrdinalIgnoreCase)))
            return $"Excluded because the source lifecycle is {lifecycle}.";

        if (ReadString(value, "supersededBy") is not null ||
            ReadString(value, "supersededByMemoryId") is not null ||
            ReadString(value, "supersededByArtifactId") is not null)
            return "Excluded because the source has been superseded.";

        return ExpiresAt(value) is { } expiresAt && expiresAt <= now
            ? "Excluded because the source is expired." : null;
    }

    public static DateTimeOffset? ExpiresAt(JsonElement value) =>
        ReadString(value, "expiresAt") is { } text &&
        DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out var result)
            ? result : null;

    private static string? ReadString(JsonElement value, string propertyName) =>
        value.ValueKind == JsonValueKind.Object && value.TryGetProperty(propertyName, out var property) &&
        property.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(property.GetString())
            ? property.GetString()!.Trim() : null;
}
