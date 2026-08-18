using iRoute.Common;

namespace iRoute.Services;

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
