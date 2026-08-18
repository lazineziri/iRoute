using iRoute.Common;

namespace iRoute.Services;

public sealed class DirectPathSelector(
    ICapabilityMatcher matcher,
    IEscalationPolicy escalationPolicy) : IDirectPathSelector
{
    public async Task<RoutingResult?> TrySelectAsync(
        TaskRequest request,
        TaskDefinition definition,
        CancellationToken cancellationToken)
    {
        var required = definition.EffectiveRequiredCapabilities;
        if (required.Count != 1)
        {
            return null;
        }

        var qualityFloor = RoutingBudgets.QualityFloor(request, definition);
        var match = await matcher.MatchAsync(request, definition, required[0], cancellationToken);
        var selection = escalationPolicy.SelectCandidate(required[0], qualityFloor, match.Candidates);
        var budget = RoutingBudgets.Create(request, definition, 1);
        var selected = selection.Selected;
        var plan = new ExecutionPlan(
            $"{definition.TaskType}@{definition.Version}:direct:{selected.ProfileId ?? selected.Capability}",
            1,
            definition.TaskType,
            definition.Version,
            [CreateStep(
                "execute",
                selected,
                [],
                budget.DeadlineMilliseconds,
                RetryAttempts(selected, definition, budget, 1))],
            budget);
        var decision = RoutingDecisions.Create(
            RoutingPath.Direct,
            "The task requires one capability, so the direct path avoids the planning tax.",
            qualityFloor,
            [selected],
            match.Candidates,
            false,
            0,
            selection.Escalated,
            selection.EscalationReason);
        return new RoutingResult(plan, decision);
    }

    internal static ExecutionPlanStep CreateStep(
        string id,
        CapabilityCandidate candidate,
        IReadOnlyList<string> dependencies,
        int timeoutMilliseconds,
        int maxAttempts) =>
        new(
            id,
            candidate.StepKind,
            candidate.Capability,
            dependencies,
            candidate.SideEffectClass,
            timeoutMilliseconds,
            maxAttempts,
            candidate.ProfileId);

    internal static int RetryAttempts(
        CapabilityCandidate candidate,
        TaskDefinition definition,
        ExecutionPlanBudget budget,
        int stepsOfKind)
    {
        if (candidate.StepKind == ExecutionStepKind.Model)
        {
            // W18 assigns model retry/fallback ownership to the provider-neutral resilience gateway.
            // Repeating the whole deployment sequence in the workflow scheduler would duplicate retries.
            return 1;
        }

        var callBudget = candidate.StepKind switch
        {
            ExecutionStepKind.Tool => budget.MaxToolCalls,
            _ => 1
        };
        return Math.Clamp(
            Math.Min(definition.DefaultMaxAttempts, Math.Max(1, callBudget / Math.Max(1, stepsOfKind))),
            1,
            5);
    }
}
