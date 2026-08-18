using iRoute.Common;

namespace iRoute.Services;

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
