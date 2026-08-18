using iRoute.Common;

namespace iRoute.Services;

internal static class RoutingDecisions
{
    public const string PolicyVersion = "routing.w18.v1";

    public static RoutingDecision Create(
        RoutingPath path,
        string reason,
        decimal qualityFloor,
        IReadOnlyList<CapabilityCandidate> selected,
        IReadOnlyList<CapabilityCandidate> candidates,
        bool plannerInvoked,
        int planningCalls,
        bool escalated,
        string? escalationReason)
    {
        var final = selected[^1];
        return new RoutingDecision(
            PolicyVersion,
            path,
            reason,
            final.Capability,
            final.ProfileId,
            final.ModelTier,
            qualityFloor,
            selected.Min(item => item.ExpectedQuality),
            selected.Sum(item => item.ExpectedCost),
            selected.Sum(item => item.ExpectedLatencyMilliseconds),
            selected.Max(item => item.Uncertainty),
            selected.Sum(item => item.Score),
            plannerInvoked,
            planningCalls,
            escalated,
            escalationReason,
            candidates.Select(item => item.ToContract()).ToArray());
    }
}
internal static class RoutingBudgets
{
    public static ExecutionPlanBudget Create(
        TaskRequest request,
        TaskDefinition definition,
        int requiredSteps) =>
        new(
            requiredSteps,
            MaximumModelCalls(request, definition),
            MaximumToolCalls(request, definition),
            Math.Min(
                request.Constraints?.MaxParallelCalls ?? definition.DefaultMaxParallelCalls,
                definition.DefaultMaxParallelCalls),
            Math.Min(
                request.Constraints?.MaxTaskDepth ?? definition.DefaultMaxTaskDepth,
                definition.DefaultMaxTaskDepth),
            Deadline(request, definition),
            MaximumInputTokens(request, definition),
            MaximumOutputTokens(request, definition),
            MaximumCost(request, definition));

    public static decimal QualityFloor(TaskRequest request, TaskDefinition definition) =>
        Math.Max(request.Constraints?.MinimumQuality ?? definition.MinimumQuality, definition.MinimumQuality);

    public static int MaximumModelCalls(TaskRequest request, TaskDefinition definition) =>
        Math.Min(
            request.Constraints?.MaxModelCalls ?? definition.DefaultMaxModelCalls,
            definition.DefaultMaxModelCalls);

    public static int MaximumToolCalls(TaskRequest request, TaskDefinition definition) =>
        Math.Min(
            request.Constraints?.MaxToolCalls ?? definition.DefaultMaxToolCalls,
            definition.DefaultMaxToolCalls);

    public static int MaximumInputTokens(TaskRequest request, TaskDefinition definition) =>
        Math.Min(
            request.Constraints?.MaxInputTokens ?? definition.DefaultMaxInputTokens,
            definition.DefaultMaxInputTokens);

    public static int MaximumOutputTokens(TaskRequest request, TaskDefinition definition) =>
        Math.Min(
            request.Constraints?.MaxOutputTokens ?? definition.DefaultMaxOutputTokens,
            definition.DefaultMaxOutputTokens);

    public static int Deadline(TaskRequest request, TaskDefinition definition) =>
        Math.Min(
            request.Constraints?.DeadlineMilliseconds ?? definition.DefaultDeadlineMilliseconds,
            definition.DefaultDeadlineMilliseconds);

    public static decimal? MaximumCost(TaskRequest request, TaskDefinition definition) =>
        (request.Constraints?.MaxCost, definition.DefaultMaxCost) switch
        {
            ({ } requestCost, { } definitionCost) => Math.Min(requestCost, definitionCost),
            ({ } requestCost, null) => requestCost,
            (null, { } definitionCost) => definitionCost,
            _ => null
        };
}
