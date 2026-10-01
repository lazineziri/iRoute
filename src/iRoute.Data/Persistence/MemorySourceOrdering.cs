using System.Text.Json;

namespace iRoute.Data;

internal static class MemorySourceOrdering
{
    // The local lineage version is not the producer's version embedded in the value.
    public static bool CannotReplace(JsonElement proposed, JsonElement current) =>
        Version(current) is > 0 and var currentVersion && Version(proposed) <= currentVersion;

    private static int Version(JsonElement value) =>
        value.ValueKind == JsonValueKind.Object && value.TryGetProperty("version", out var property) &&
        property.ValueKind == JsonValueKind.Number && property.TryGetInt32(out var version) && version > 0
            ? version : 0;
}
