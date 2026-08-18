using iRoute.Common;

namespace iRoute.Services;

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
