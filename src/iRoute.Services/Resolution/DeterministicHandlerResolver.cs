using iRoute.Common;

namespace iRoute.Services;

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
