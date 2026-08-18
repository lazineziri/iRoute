using System.Text.Json;
using iRoute.Common;

namespace iRoute.Services;

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
