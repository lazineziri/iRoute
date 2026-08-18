using System.Text.Json;
using iRoute.Common;

namespace iRoute.Services;

internal static class ResolutionChecks
{
    public static ResolutionDecision? PermissionRejection(
        TaskRequest request,
        TaskDefinition definition)
    {
        var granted = (request.PermissionScopes ?? []).ToHashSet(StringComparer.Ordinal);
        var missing = definition.EffectivePermissionScopes
            .Where(scope => !granted.Contains(scope))
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
        return missing.Length == 0
            ? null
            : Rejected(
                ResolutionDecisionCodes.PermissionDenied,
                $"No-model reuse was denied because required permission scopes are missing: {string.Join(", ", missing)}.",
                permissionChecked: true,
                freshnessChecked: false,
                "Authenticated permission scopes were checked before state lookup.");
    }

    public static ResolutionDecision Accepted(
        string code,
        string reason,
        ResolutionCandidate candidate,
        params string[] checks) =>
        new(true, code, reason, true, true, checks, candidate);

    public static ResolutionDecision Rejected(
        string code,
        string reason,
        bool permissionChecked,
        bool freshnessChecked,
        params string[] checks) =>
        new(false, code, reason, permissionChecked, freshnessChecked, checks);

    public static string ArtifactLogicalKey(TaskRequest request)
    {
        var key = request.Metadata?.GetValueOrDefault("artifactKey")?.Trim();
        return string.IsNullOrWhiteSpace(key) ? request.TaskType : key;
    }

    public static string? ReadInputString(JsonElement input, string propertyName) =>
        input.ValueKind == JsonValueKind.Object &&
        input.TryGetProperty(propertyName, out var value) &&
        value.ValueKind == JsonValueKind.String
            ? value.GetString()?.Trim()
            : null;

    public static bool IsEligibleArtifact(
        ArtifactRecord? artifact,
        TaskRequest request,
        TaskDefinition definition,
        DateTimeOffset at) =>
        artifact is not null &&
        string.Equals(artifact.ProjectId ?? string.Empty, request.ProjectId ?? string.Empty, StringComparison.Ordinal) &&
        string.Equals(artifact.TaskType, request.TaskType, StringComparison.Ordinal) &&
        artifact.TaskDefinitionVersion == definition.Version &&
        string.Equals(artifact.ArtifactType, definition.ArtifactType, StringComparison.Ordinal) &&
        artifact.IsActive &&
        artifact.LifecycleStatus == ArtifactLifecycleStatus.Active &&
        (artifact.ExpiresAt is null || artifact.ExpiresAt > at);

    public static EvidenceReference[] ArtifactEvidence(ArtifactRecord artifact) =>
        artifact.Evidence
            .Append(new EvidenceReference(
                "artifact",
                artifact.ArtifactId.ToString(),
                artifact.ContentHash,
                artifact.CreatedAt))
            .DistinctBy(item => (item.Kind, item.Reference))
            .ToArray();
}
