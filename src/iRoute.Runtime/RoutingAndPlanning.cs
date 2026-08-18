using iRoute.Contracts;
using iRoute.Core;

namespace iRoute.Runtime;

public sealed class TaskRouter(
    IDirectPathSelector directPath,
    IBoundedTaskPlanner planner) : ITaskRouter
{
    public async Task<RoutingResult> RouteAsync(
        TaskRequest request,
        TaskDefinition definition,
        CancellationToken cancellationToken)
    {
        var direct = await directPath.TrySelectAsync(request, definition, cancellationToken);
        return direct ?? await planner.PlanAsync(request, definition, cancellationToken);
    }
}

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

public sealed class BoundedTaskPlanner(
    ICapabilityMatcher matcher,
    IEscalationPolicy escalationPolicy,
    IExecutionPlanValidator validator) : IBoundedTaskPlanner
{
    private const int MaximumSteps = 32;

    public async Task<RoutingResult> PlanAsync(
        TaskRequest request,
        TaskDefinition definition,
        CancellationToken cancellationToken)
    {
        var required = definition.EffectiveRequiredCapabilities;
        if (required.Count is < 2 or > MaximumSteps)
        {
            throw new RoutingException(
                ErrorCodes.RoutingBudgetExceeded,
                "Routing budget exceeded",
                $"A workflow route must contain between 2 and {MaximumSteps} required capabilities.");
        }

        if (definition.SideEffectClass >= SideEffectClass.ReversibleWrite)
        {
            throw new RoutingException(
                ErrorCodes.RoutingNoEligibleCapability,
                "No eligible workflow route",
                "Multi-capability external-write workflows require an explicit trusted action plan.");
        }

        var qualityFloor = RoutingBudgets.QualityFloor(request, definition);
        var matches = new List<CapabilityMatchResult>(required.Count);
        var selections = new List<CapabilitySelection>(required.Count);
        foreach (var capability in required)
        {
            var match = await matcher.MatchAsync(request, definition, capability, cancellationToken);
            matches.Add(match);
            selections.Add(escalationPolicy.SelectCandidate(capability, qualityFloor, match.Candidates));
        }

        var budget = RoutingBudgets.Create(request, definition, required.Count);
        EnsureWithinBudget(selections.Select(item => item.Selected).ToArray(), budget);
        var selectedCandidates = selections.Select(item => item.Selected).ToArray();
        var modelSteps = selectedCandidates.Count(item => item.StepKind == ExecutionStepKind.Model);
        var toolSteps = selectedCandidates.Count(item => item.StepKind == ExecutionStepKind.Tool);
        var steps = new List<ExecutionPlanStep>(required.Count);
        for (var index = 0; index < selections.Count; index++)
        {
            var id = index == selections.Count - 1 ? "execute" : $"step-{index + 1}";
            var dependencies = index == 0 ? [] : new[] { steps[index - 1].Id };
            steps.Add(DirectPathSelector.CreateStep(
                id,
                selections[index].Selected,
                dependencies,
                budget.DeadlineMilliseconds,
                DirectPathSelector.RetryAttempts(
                    selections[index].Selected,
                    definition,
                    budget,
                    selections[index].Selected.StepKind == ExecutionStepKind.Model
                        ? modelSteps
                        : toolSteps)));
        }

        var plan = new ExecutionPlan(
            $"{definition.TaskType}@{definition.Version}:workflow",
            1,
            definition.TaskType,
            definition.Version,
            steps,
            budget);
        try
        {
            validator.EnsureValid(plan);
        }
        catch (InvalidExecutionPlanException exception)
        {
            throw new RoutingException(
                ErrorCodes.RoutingBudgetExceeded,
                "Routing budget exceeded",
                exception.Message);
        }

        var candidates = matches.SelectMany(item => item.Candidates).ToArray();
        var selected = selections.Select(item => item.Selected).ToArray();
        var escalated = selections.Any(item => item.Escalated);
        var escalationReason = string.Join(
            " ",
            selections
                .Where(item => item.Escalated && !string.IsNullOrWhiteSpace(item.EscalationReason))
                .Select(item => item.EscalationReason));
        var decision = RoutingDecisions.Create(
            RoutingPath.Workflow,
            $"The task requires {required.Count} capabilities, so one bounded planner invocation compiled a typed DAG.",
            qualityFloor,
            selected,
            candidates,
            true,
            1,
            escalated,
            string.IsNullOrWhiteSpace(escalationReason) ? null : escalationReason);
        return new RoutingResult(plan, decision);
    }

    private static void EnsureWithinBudget(
        CapabilityCandidate[] selected,
        ExecutionPlanBudget budget)
    {
        var modelCalls = selected.Count(item => item.StepKind == ExecutionStepKind.Model);
        var toolCalls = selected.Count(item => item.StepKind == ExecutionStepKind.Tool);
        if (selected.Length > budget.MaxSteps ||
            selected.Length > budget.MaxTaskDepth ||
            modelCalls > budget.MaxModelCalls ||
            toolCalls > budget.MaxToolCalls)
        {
            throw new RoutingException(
                ErrorCodes.RoutingBudgetExceeded,
                "Routing budget exceeded",
                $"The workflow requires depth {selected.Length}, {modelCalls} model calls and {toolCalls} tool calls, " +
                $"but the task allows depth {budget.MaxTaskDepth}, {budget.MaxModelCalls} model calls and {budget.MaxToolCalls} tool calls.");
        }
    }
}

public sealed class MeasuredCapabilityMatcher(IModelProfileRegistry modelProfiles) : ICapabilityMatcher
{
    public async Task<CapabilityMatchResult> MatchAsync(
        TaskRequest request,
        TaskDefinition definition,
        string capability,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var profiles = await modelProfiles.ListAsync(capability, cancellationToken);
        if (profiles.Count > 0)
        {
            return new CapabilityMatchResult(
                capability,
                profiles
                    .Select(profile => EvaluateModel(request, definition, profile))
                    .OrderBy(item => item.ExpectedCost)
                    .ThenBy(item => item.ExpectedLatencyMilliseconds)
                    .ThenByDescending(item => item.ExpectedQuality)
                    .ToArray());
        }

        return new CapabilityMatchResult(
            capability,
            [EvaluateNonModel(request, definition, capability)]);
    }

    private static CapabilityCandidate EvaluateModel(
        TaskRequest request,
        TaskDefinition definition,
        ModelProfile profile)
    {
        var failures = new List<string>();
        var qualityFloor = RoutingBudgets.QualityFloor(request, definition);
        var maxInputTokens = RoutingBudgets.MaximumInputTokens(request, definition);
        var maxOutputTokens = RoutingBudgets.MaximumOutputTokens(request, definition);
        var deadline = RoutingBudgets.Deadline(request, definition);
        var maxCost = RoutingBudgets.MaximumCost(request, definition);
        if (!definition.EffectiveAllowedCapabilities.Contains(profile.Capability, StringComparer.Ordinal))
        {
            failures.Add("capability is not allow-listed");
        }
        if (!profile.SupportedTaskTypes.Contains(definition.TaskType, StringComparer.Ordinal))
        {
            failures.Add("task type has no measured profile");
        }
        if (!profile.Healthy || profile.Availability <= 0m)
        {
            failures.Add("profile is unavailable");
        }
        if (profile.ExpectedQuality < qualityFloor)
        {
            failures.Add($"expected quality {profile.ExpectedQuality:0.###} is below floor {qualityFloor:0.###}");
        }
        if (profile.ExpectedLatencyMilliseconds > deadline)
        {
            failures.Add($"expected latency exceeds the {deadline} ms deadline");
        }
        if (profile.MaxInputTokens < maxInputTokens || profile.MaxOutputTokens < maxOutputTokens)
        {
            failures.Add("profile token capacity is below the task budget");
        }
        if (maxCost is { } cost && profile.EstimatedCost > cost)
        {
            failures.Add($"estimated cost exceeds the {cost:0.####} ceiling");
        }
        if (RoutingBudgets.MaximumModelCalls(request, definition) == 0)
        {
            failures.Add("model-call budget is zero");
        }
        ValidateMeasurementProvenance(profile, failures);

        return new CapabilityCandidate(
            profile.Capability,
            ExecutionStepKind.Model,
            SideEffectClass.None,
            profile.ProfileId,
            profile.Tier,
            failures.Count == 0,
            failures.Count == 0
                ? $"eligible {profile.MeasurementSource.ToString().ToLowerInvariant()} model profile"
                : string.Join("; ", failures),
            profile.ExpectedQuality,
            profile.EstimatedCost,
            profile.ExpectedLatencyMilliseconds,
            profile.Uncertainty,
            profile.Reliability,
            profile.Availability,
            Score(definition.EffectiveRoutingWeights, profile),
            profile.MeasurementSource,
            profile.Measurement);
    }

    private static void ValidateMeasurementProvenance(
        ModelProfile profile,
        List<string> failures)
    {
        if (profile.MeasurementSource != ModelProfileSource.Measured)
        {
            if (profile.Measurement is not null)
            {
                failures.Add("only measured profiles may carry measurement metadata");
            }
            return;
        }

        if (profile.Measurement is null)
        {
            failures.Add("measured profile is missing measurement metadata");
            return;
        }

        if (string.IsNullOrWhiteSpace(profile.Measurement.Provider) ||
            string.IsNullOrWhiteSpace(profile.Measurement.Model) ||
            profile.Measurement.MeasuredAt == default ||
            profile.Measurement.SampleCount <= 0)
        {
            failures.Add("measured profile has invalid measurement metadata");
        }
    }

    private static CapabilityCandidate EvaluateNonModel(
        TaskRequest request,
        TaskDefinition definition,
        string capability)
    {
        var failures = new List<string>();
        if (!definition.EffectiveAllowedCapabilities.Contains(capability, StringComparer.Ordinal))
        {
            failures.Add("capability is not allow-listed");
        }
        if (RoutingBudgets.MaximumToolCalls(request, definition) == 0)
        {
            failures.Add("tool-call budget is zero");
        }

        const decimal expectedQuality = 1m;
        const decimal estimatedCost = 0m;
        const int expectedLatency = 25;
        const decimal uncertainty = 0m;
        var weights = definition.EffectiveRoutingWeights;
        var score = weights.Quality * expectedQuality -
            weights.Cost * estimatedCost -
            weights.Latency * expectedLatency -
            weights.Uncertainty * uncertainty;
        return new CapabilityCandidate(
            capability,
            ExecutionStepKind.Tool,
            definition.SideEffectClass,
            null,
            null,
            failures.Count == 0,
            failures.Count == 0 ? "eligible registered non-model capability" : string.Join("; ", failures),
            expectedQuality,
            estimatedCost,
            expectedLatency,
            uncertainty,
            1m,
            1m,
            score);
    }

    private static decimal Score(RoutingWeights weights, ModelProfile profile) =>
        weights.Quality * profile.ExpectedQuality -
        weights.Cost * profile.EstimatedCost -
        weights.Latency * profile.ExpectedLatencyMilliseconds -
        weights.Uncertainty * profile.Uncertainty;
}

public sealed class MeasuredEscalationPolicy : IEscalationPolicy
{
    public CapabilitySelection SelectCandidate(
        string capability,
        decimal qualityFloor,
        IReadOnlyList<CapabilityCandidate> candidates)
    {
        var ordered = candidates
            .OrderBy(item => item.ExpectedCost)
            .ThenBy(item => item.ExpectedLatencyMilliseconds)
            .ThenByDescending(item => item.ExpectedQuality)
            .ToArray();
        var selected = ordered.FirstOrDefault(item => item.Eligible);
        if (selected is null)
        {
            var reasons = string.Join(
                " | ",
                ordered.Select(item => $"{item.ProfileId ?? item.Capability}: {item.Reason}"));
            throw new RoutingException(
                ordered.Any(item => item.StepKind == ExecutionStepKind.Model &&
                    item.Reason.Contains("model-call budget is zero", StringComparison.Ordinal))
                    ? ErrorCodes.ModelBudgetExhausted
                    : ErrorCodes.RoutingNoEligibleCapability,
                "No eligible capability",
                $"No measured route for '{capability}' satisfies quality floor {qualityFloor:0.###} and task policy. {reasons}".Trim());
        }

        var bypassed = ordered.TakeWhile(item => !ReferenceEquals(item, selected)).ToArray();
        if (bypassed.Length == 0)
        {
            return new CapabilitySelection(selected, false, null);
        }

        var first = bypassed[0];
        return new CapabilitySelection(
            selected,
            true,
            $"Escalated past lower-cost route '{first.ProfileId ?? first.Capability}' because {first.Reason}.");
    }
}

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
