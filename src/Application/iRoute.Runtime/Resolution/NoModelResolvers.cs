using System.Text.Json;
using iRoute.Contracts;
using iRoute.Core;

namespace iRoute.Runtime;

public sealed class ExactResultResolver(
    IArtifactStore artifacts,
    IInputFingerprint fingerprint,
    TimeProvider clock) : INoModelResolver
{
    public string Name => "exact-cache";
    public int Order => 0;

    public async Task<ResolutionDecision> ResolveAsync(
        TaskRequest request,
        TaskDefinition definition,
        CancellationToken cancellationToken)
    {
        if (ResolutionChecks.PermissionRejection(request, definition) is { } rejection)
        {
            return rejection;
        }

        var logicalKey = ResolutionChecks.ArtifactLogicalKey(request);
        var artifact = await artifacts.FindReusableAsync(
            new ArtifactReuseQuery(
                RequestScope.Tenant(request),
                request.ProjectId,
                request.TaskType,
                definition.Version,
                fingerprint.Create(request, definition.Version),
                logicalKey,
                clock.GetUtcNow()),
            cancellationToken);
        if (artifact is null)
        {
            return ResolutionChecks.Rejected(
                ResolutionDecisionCodes.ExactCacheMiss,
                "No active, fresh artifact matched the task version, logical key, and exact input fingerprint.",
                permissionChecked: true,
                freshnessChecked: true,
                "Authenticated permission scopes were checked.",
                "Tenant, project, task version, logical key, freshness, and input fingerprint were checked.");
        }

        return ResolutionChecks.Accepted(
            ResolutionDecisionCodes.ExactCacheHit,
            "An active artifact matched the exact scoped input fingerprint.",
            new ResolutionCandidate(
                ResolutionLevel.ExactArtifact,
                artifact.Content.Clone(),
                1m,
                ResolutionChecks.ArtifactEvidence(artifact),
                artifact.ToReference()),
            "Authenticated permission scopes were checked.",
            "Tenant and project scope matched.",
            "Task definition version and logical key matched.",
            "The artifact is active and unexpired.",
            "The input fingerprint matched exactly.");
    }
}

public sealed class FactDecisionResolver(
    IMemoryStore memories,
    TimeProvider clock) : INoModelResolver
{
    public string Name => "fact-decision";
    public int Order => 10;

    public async Task<ResolutionDecision> ResolveAsync(
        TaskRequest request,
        TaskDefinition definition,
        CancellationToken cancellationToken)
    {
        if (ResolutionChecks.PermissionRejection(request, definition) is { } rejection)
        {
            return rejection;
        }

        var kind = definition.TaskType switch
        {
            "project.decision.get" => MemoryKind.Decision,
            "project.fact.get" => MemoryKind.Fact,
            _ => (MemoryKind?)null
        };
        if (kind is null)
        {
            return ResolutionChecks.Rejected(
                ResolutionDecisionCodes.UnsupportedTask,
                "This resolver only handles typed project fact and decision lookups.",
                permissionChecked: true,
                freshnessChecked: false,
                "Authenticated permission scopes were checked.");
        }

        if (string.IsNullOrWhiteSpace(request.ProjectId))
        {
            return ResolutionChecks.Rejected(
                ResolutionDecisionCodes.ProjectScopeRequired,
                "A project-scoped state lookup requires projectId.",
                permissionChecked: true,
                freshnessChecked: false,
                "Authenticated permission scopes were checked.");
        }

        var key = ResolutionChecks.ReadInputString(request.Input, "key");
        if (string.IsNullOrWhiteSpace(key))
        {
            return ResolutionChecks.Rejected(
                ResolutionDecisionCodes.StateKeyRequired,
                "A project state lookup requires a non-empty input key.",
                permissionChecked: true,
                freshnessChecked: false,
                "Authenticated permission scopes were checked.",
                "The typed state lookup input was checked.");
        }

        var memory = await memories.GetActiveAsync(
            new MemoryLookup(
                RequestScope.Tenant(request),
                request.ProjectId,
                kind.Value,
                key.Trim(),
                clock.GetUtcNow()),
            cancellationToken);
        if (memory is null)
        {
            return ResolutionChecks.Rejected(
                ResolutionDecisionCodes.StateUnavailable,
                "No active, fresh state matched the requested tenant, project, kind, and key.",
                permissionChecked: true,
                freshnessChecked: true,
                "Authenticated permission scopes were checked.",
                "Tenant, project, state kind, key, lifecycle, and freshness were checked.");
        }

        var output = JsonSerializer.SerializeToElement(new
        {
            kind = memory.Kind,
            key = memory.Key,
            version = memory.Version,
            value = memory.Value,
            contentHash = memory.ContentHash,
            createdAt = memory.CreatedAt
        });
        var evidence = memory.Evidence
            .Append(new EvidenceReference(
                "memory",
                memory.MemoryId.ToString(),
                memory.ContentHash,
                memory.CreatedAt))
            .DistinctBy(item => (item.Kind, item.Reference))
            .ToArray();
        return ResolutionChecks.Accepted(
            ResolutionDecisionCodes.StateHit,
            "An active project state record matched the typed lookup.",
            new ResolutionCandidate(
                ResolutionLevel.StructuredState,
                output,
                1m,
                evidence),
            "Authenticated permission scopes were checked.",
            "Tenant and project scope matched.",
            "State kind and key matched.",
            "The state record is active and unexpired.");
    }
}

public sealed class ArtifactLookupResolver(
    IArtifactStore artifacts,
    TimeProvider clock) : INoModelResolver
{
    public string Name => "artifact-lookup";
    public int Order => 20;

    public async Task<ResolutionDecision> ResolveAsync(
        TaskRequest request,
        TaskDefinition definition,
        CancellationToken cancellationToken)
    {
        if (ResolutionChecks.PermissionRejection(request, definition) is { } rejection)
        {
            return rejection;
        }

        var artifactIdValue = ResolutionChecks.ReadInputString(request.Input, "artifactId")
            ?? request.Metadata?.GetValueOrDefault("artifactId");
        var logicalKey = ResolutionChecks.ReadInputString(request.Input, "artifactKey")
            ?? request.Metadata?.GetValueOrDefault("artifactKey");
        ArtifactRecord? artifact;
        if (Guid.TryParse(artifactIdValue, out var artifactId))
        {
            artifact = await artifacts.GetAsync(
                RequestScope.Tenant(request),
                artifactId,
                cancellationToken);
            if (!ResolutionChecks.IsEligibleArtifact(artifact, request, definition, clock.GetUtcNow()))
            {
                artifact = null;
            }
        }
        else if (!string.IsNullOrWhiteSpace(logicalKey))
        {
            artifact = await artifacts.FindActiveAsync(
                new ArtifactLookupQuery(
                    RequestScope.Tenant(request),
                    request.ProjectId,
                    request.TaskType,
                    definition.Version,
                    definition.ArtifactType,
                    logicalKey.Trim(),
                    clock.GetUtcNow()),
                cancellationToken);
        }
        else
        {
            return ResolutionChecks.Rejected(
                ResolutionDecisionCodes.ArtifactReferenceRequired,
                "No explicit artifactId or artifactKey was supplied for artifact lookup.",
                permissionChecked: true,
                freshnessChecked: false,
                "Authenticated permission scopes were checked.",
                "Explicit artifact lookup input was checked.");
        }

        if (artifact is null)
        {
            return ResolutionChecks.Rejected(
                ResolutionDecisionCodes.ArtifactUnavailable,
                "No active, fresh artifact matched the explicit reference and requested scope.",
                permissionChecked: true,
                freshnessChecked: true,
                "Authenticated permission scopes were checked.",
                "Tenant, project, task version, artifact type, lifecycle, and freshness were checked.");
        }

        return ResolutionChecks.Accepted(
            ResolutionDecisionCodes.ArtifactHit,
            "An active artifact matched the explicit scoped reference.",
            new ResolutionCandidate(
                ResolutionLevel.ExactArtifact,
                artifact.Content.Clone(),
                1m,
                ResolutionChecks.ArtifactEvidence(artifact),
                artifact.ToReference()),
            "Authenticated permission scopes were checked.",
            "Tenant and project scope matched.",
            "Task definition version and artifact type matched.",
            "The artifact is active and unexpired.");
    }
}

public sealed class DeterministicHandlerResolver(
    IEnumerable<IDeterministicTaskHandler> handlers,
    TimeProvider clock) : INoModelResolver
{
    public string Name => "deterministic-handler";
    public int Order => 30;

    public async Task<ResolutionDecision> ResolveAsync(
        TaskRequest request,
        TaskDefinition definition,
        CancellationToken cancellationToken)
    {
        if (ResolutionChecks.PermissionRejection(request, definition) is { } rejection)
        {
            return rejection;
        }

        var handler = handlers
            .Where(item =>
                item.Supports(definition) &&
                definition.EffectiveAllowedCapabilities.Contains(item.Capability, StringComparer.Ordinal))
            .OrderBy(item => item.Name, StringComparer.Ordinal)
            .FirstOrDefault();
        if (handler is null)
        {
            return ResolutionChecks.Rejected(
                ResolutionDecisionCodes.HandlerUnavailable,
                "No deterministic handler is registered for this task definition.",
                permissionChecked: true,
                freshnessChecked: false,
                "Authenticated permission scopes were checked.",
                "The deterministic handler registry was checked.");
        }

        var result = await handler.TryResolveAsync(request, definition, cancellationToken);
        if (result is null)
        {
            return ResolutionChecks.Rejected(
                ResolutionDecisionCodes.HandlerDeclined,
                $"Deterministic handler '{handler.Name}' could not resolve the supplied input.",
                permissionChecked: true,
                freshnessChecked: true,
                "Authenticated permission scopes were checked.",
                "The matching deterministic handler evaluated the input.");
        }

        if (result.ExpiresAt is { } expiresAt && expiresAt <= clock.GetUtcNow())
        {
            return ResolutionChecks.Rejected(
                ResolutionDecisionCodes.HandlerStale,
                $"Deterministic handler '{handler.Name}' returned a stale result.",
                permissionChecked: true,
                freshnessChecked: true,
                "Authenticated permission scopes were checked.",
                "The deterministic result freshness boundary was checked.");
        }

        var evidence = result.Evidence
            .Append(new EvidenceReference(
                "deterministic-handler",
                handler.Name,
                CanonicalJson.Hash(result.Output),
                clock.GetUtcNow()))
            .DistinctBy(item => (item.Kind, item.Reference))
            .ToArray();
        return ResolutionChecks.Accepted(
            ResolutionDecisionCodes.HandlerAccepted,
            $"Deterministic handler '{handler.Name}' resolved the task without generation.",
            new ResolutionCandidate(
                ResolutionLevel.DeterministicCapability,
                result.Output.Clone(),
                result.Confidence,
                evidence,
                Usage: new UsageSummary(ToolCalls: 1)),
            (result.Checks ?? [])
                .Prepend("The deterministic result is fresh.")
                .Prepend("Authenticated permission scopes were checked.")
                .ToArray());
    }
}

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
