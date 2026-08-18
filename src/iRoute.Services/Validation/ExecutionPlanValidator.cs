using iRoute.Common;

namespace iRoute.Services;

public sealed class ExecutionPlanValidator : IExecutionPlanValidator
{
    private const int MaximumSteps = 32;
    private const int MaximumModelCalls = 16;
    private const int MaximumToolCalls = 64;
    private const int MaximumParallelCalls = 32;
    private const int MaximumTaskDepth = 16;
    private const int MaximumDeadlineMilliseconds = 900_000;

    public ExecutionPlanValidationResult Validate(ExecutionPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        var issues = new List<ExecutionPlanValidationIssue>();

        ValidateHeader(plan, issues);
        ValidateBudget(plan.Budget, issues);

        var stepsById = new Dictionary<string, ExecutionPlanStep>(StringComparer.Ordinal);
        for (var index = 0; index < plan.Steps.Count; index++)
        {
            var step = plan.Steps[index];
            var path = $"steps[{index}]";
            ValidateStep(step, path, issues);
            if (step.TimeoutMilliseconds > plan.Budget.DeadlineMilliseconds)
            {
                issues.Add(new(
                    "deadline_budget_exceeded",
                    $"{path}.timeoutMilliseconds",
                    "A step timeout cannot exceed the plan deadline."));
            }
            if (!string.IsNullOrWhiteSpace(step.Id) && !stepsById.TryAdd(step.Id, step))
            {
                issues.Add(new(
                    "duplicate_step_id",
                    $"{path}.id",
                    $"Step id '{step.Id}' is declared more than once."));
            }
        }

        foreach (var (stepId, step) in stepsById)
        {
            foreach (var dependency in step.DependsOn)
            {
                if (!stepsById.ContainsKey(dependency))
                {
                    issues.Add(new(
                        "unknown_dependency",
                        $"steps.{stepId}.dependsOn",
                        $"Dependency '{dependency}' does not name a step in this plan."));
                }
            }
        }

        if (plan.Steps.Count > plan.Budget.MaxSteps)
        {
            issues.Add(new(
                "step_budget_exceeded",
                "budget.maxSteps",
                $"The plan declares {plan.Steps.Count} steps but allows only {plan.Budget.MaxSteps}."));
        }

        var potentialModelCalls = plan.Steps
            .Where(step => step.Kind == ExecutionStepKind.Model)
            .Sum(step => Math.Max(0, step.MaxAttempts));
        if (potentialModelCalls > plan.Budget.MaxModelCalls)
        {
            issues.Add(new(
                "model_call_budget_exceeded",
                "budget.maxModelCalls",
                $"The plan can make {potentialModelCalls} model calls but allows only {plan.Budget.MaxModelCalls}."));
        }

        var potentialToolCalls = plan.Steps
            .Where(step => step.Kind == ExecutionStepKind.Tool)
            .Sum(step => Math.Max(0, step.MaxAttempts));
        if (potentialToolCalls > plan.Budget.MaxToolCalls)
        {
            issues.Add(new(
                "tool_call_budget_exceeded",
                "budget.maxToolCalls",
                $"The plan can make {potentialToolCalls} tool calls but allows only {plan.Budget.MaxToolCalls}."));
        }

        ValidateGraph(stepsById, plan.Budget.MaxTaskDepth, issues);
        return new(issues);
    }

    public void EnsureValid(ExecutionPlan plan)
    {
        var result = Validate(plan);
        if (!result.IsValid)
        {
            throw new InvalidExecutionPlanException(result.Issues);
        }
    }

    private static void ValidateHeader(
        ExecutionPlan plan,
        List<ExecutionPlanValidationIssue> issues)
    {
        if (string.IsNullOrWhiteSpace(plan.PlanId))
        {
            issues.Add(new("required", "planId", "PlanId is required."));
        }

        if (plan.Version < 1)
        {
            issues.Add(new("out_of_range", "version", "Version must be at least one."));
        }

        if (string.IsNullOrWhiteSpace(plan.TaskType))
        {
            issues.Add(new("required", "taskType", "TaskType is required."));
        }

        if (plan.TaskVersion < 1)
        {
            issues.Add(new("out_of_range", "taskVersion", "TaskVersion must be at least one."));
        }

        if (plan.Steps.Count is < 1 or > MaximumSteps)
        {
            issues.Add(new(
                "out_of_range",
                "steps",
                $"A plan must contain between one and {MaximumSteps} steps."));
        }
    }

    private static void ValidateBudget(
        ExecutionPlanBudget budget,
        List<ExecutionPlanValidationIssue> issues)
    {
        ValidateRange(budget.MaxSteps, 1, MaximumSteps, "budget.maxSteps", issues);
        ValidateRange(budget.MaxModelCalls, 0, MaximumModelCalls, "budget.maxModelCalls", issues);
        ValidateRange(budget.MaxToolCalls, 0, MaximumToolCalls, "budget.maxToolCalls", issues);
        ValidateRange(budget.MaxParallelCalls, 1, MaximumParallelCalls, "budget.maxParallelCalls", issues);
        ValidateRange(budget.MaxTaskDepth, 1, MaximumTaskDepth, "budget.maxTaskDepth", issues);
        ValidateRange(
            budget.DeadlineMilliseconds,
            1,
            MaximumDeadlineMilliseconds,
            "budget.deadlineMilliseconds",
            issues);

        if (budget.MaxInputTokens is <= 0)
        {
            issues.Add(new("out_of_range", "budget.maxInputTokens", "MaxInputTokens must be positive."));
        }

        if (budget.MaxOutputTokens is <= 0)
        {
            issues.Add(new("out_of_range", "budget.maxOutputTokens", "MaxOutputTokens must be positive."));
        }

        if (budget.MaxCost is < 0)
        {
            issues.Add(new("out_of_range", "budget.maxCost", "MaxCost cannot be negative."));
        }
    }

    private static void ValidateStep(
        ExecutionPlanStep step,
        string path,
        List<ExecutionPlanValidationIssue> issues)
    {
        if (string.IsNullOrWhiteSpace(step.Id))
        {
            issues.Add(new("required", $"{path}.id", "Step id is required."));
        }

        if (string.IsNullOrWhiteSpace(step.Capability))
        {
            issues.Add(new("required", $"{path}.capability", "Capability is required."));
        }

        if (step.TimeoutMilliseconds is < 100 or > MaximumDeadlineMilliseconds)
        {
            issues.Add(new(
                "out_of_range",
                $"{path}.timeoutMilliseconds",
                $"Step timeout must be between 100 and {MaximumDeadlineMilliseconds} milliseconds."));
        }

        if (step.MaxAttempts is < 1 or > 5)
        {
            issues.Add(new("out_of_range", $"{path}.maxAttempts", "MaxAttempts must be between one and five."));
        }

        if (step.DependsOn.Count != step.DependsOn.Distinct(StringComparer.Ordinal).Count())
        {
            issues.Add(new("duplicate_dependency", $"{path}.dependsOn", "Dependencies must be unique."));
        }

        if (step.DependsOn.Contains(step.Id, StringComparer.Ordinal))
        {
            issues.Add(new("self_dependency", $"{path}.dependsOn", "A step cannot depend on itself."));
        }
    }

    private static void ValidateGraph(
        IReadOnlyDictionary<string, ExecutionPlanStep> stepsById,
        int maximumDepth,
        List<ExecutionPlanValidationIssue> issues)
    {
        var states = new Dictionary<string, VisitState>(StringComparer.Ordinal);
        var depths = new Dictionary<string, int>(StringComparer.Ordinal);

        foreach (var stepId in stepsById.Keys)
        {
            Visit(stepId);
        }

        if (depths.Count > 0 && depths.Values.Max() > maximumDepth)
        {
            issues.Add(new(
                "task_depth_exceeded",
                "budget.maxTaskDepth",
                $"The plan depth is {depths.Values.Max()} but allows only {maximumDepth}."));
        }

        int Visit(string stepId)
        {
            if (states.GetValueOrDefault(stepId) == VisitState.Visited)
            {
                return depths[stepId];
            }

            if (states.GetValueOrDefault(stepId) == VisitState.Visiting)
            {
                issues.Add(new(
                    "cyclic_dependency",
                    $"steps.{stepId}.dependsOn",
                    $"A dependency cycle includes step '{stepId}'."));
                return 0;
            }

            states[stepId] = VisitState.Visiting;
            var depth = 1;
            foreach (var dependency in stepsById[stepId].DependsOn)
            {
                if (stepsById.ContainsKey(dependency))
                {
                    depth = Math.Max(depth, Visit(dependency) + 1);
                }
            }

            states[stepId] = VisitState.Visited;
            depths[stepId] = depth;
            return depth;
        }
    }

    private static void ValidateRange(
        int value,
        int minimum,
        int maximum,
        string path,
        List<ExecutionPlanValidationIssue> issues)
    {
        if (value < minimum || value > maximum)
        {
            issues.Add(new(
                "out_of_range",
                path,
                $"The value must be between {minimum} and {maximum}."));
        }
    }

    private enum VisitState
    {
        Unvisited,
        Visiting,
        Visited
    }
}
