using iRoute.Contracts;
using iRoute.Core;

namespace iRoute.UnitTests;

public sealed class ExecutionPlanValidatorTests
{
    private readonly ExecutionPlanValidator _validator = new();

    [Fact]
    public void ValidDagWithinBudgetsIsAccepted()
    {
        var plan = CreatePlan(
            [
                Step("retrieve", ExecutionStepKind.Tool),
                Step("draft", ExecutionStepKind.Model, "retrieve"),
                Step("validate", ExecutionStepKind.Deterministic, "draft")
            ],
            new ExecutionPlanBudget(3, 1, 1, 2, 3, 30_000));

        var result = _validator.Validate(plan);

        Assert.True(result.IsValid);
        Assert.Empty(result.Issues);
    }

    [Fact]
    public void DuplicateAndUnknownStepReferencesAreRejected()
    {
        var plan = CreatePlan(
            [
                Step("draft", ExecutionStepKind.Model, "missing"),
                Step("draft", ExecutionStepKind.Deterministic)
            ],
            new ExecutionPlanBudget(2, 1, 0, 1, 2, 30_000));

        var result = _validator.Validate(plan);

        Assert.False(result.IsValid);
        Assert.Contains(result.Issues, issue => issue.Code == "duplicate_step_id");
        Assert.Contains(result.Issues, issue => issue.Code == "unknown_dependency");
    }

    [Fact]
    public void CyclesAreRejected()
    {
        var plan = CreatePlan(
            [
                Step("first", ExecutionStepKind.Deterministic, "second"),
                Step("second", ExecutionStepKind.Deterministic, "first")
            ],
            new ExecutionPlanBudget(2, 0, 0, 1, 2, 30_000));

        var result = _validator.Validate(plan);

        Assert.Contains(result.Issues, issue => issue.Code == "cyclic_dependency");
    }

    [Fact]
    public void PotentialAttemptsMustFitCallBudgets()
    {
        var plan = CreatePlan(
            [
                Step("draft", ExecutionStepKind.Model) with { MaxAttempts = 2 },
                Step("lookup", ExecutionStepKind.Tool) with { MaxAttempts = 3 }
            ],
            new ExecutionPlanBudget(2, 1, 2, 2, 1, 30_000));

        var result = _validator.Validate(plan);

        Assert.Contains(result.Issues, issue => issue.Code == "model_call_budget_exceeded");
        Assert.Contains(result.Issues, issue => issue.Code == "tool_call_budget_exceeded");
    }

    [Fact]
    public void DependencyDepthMustFitTaskDepthBudget()
    {
        var plan = CreatePlan(
            [
                Step("one", ExecutionStepKind.Deterministic),
                Step("two", ExecutionStepKind.Deterministic, "one"),
                Step("three", ExecutionStepKind.Deterministic, "two")
            ],
            new ExecutionPlanBudget(3, 0, 0, 1, 2, 30_000));

        var result = _validator.Validate(plan);

        Assert.Contains(result.Issues, issue => issue.Code == "task_depth_exceeded");
    }

    [Fact]
    public void StepTimeoutMustFitPlanDeadline()
    {
        var plan = CreatePlan(
            [Step("slow", ExecutionStepKind.Deterministic) with { TimeoutMilliseconds = 30_000 }],
            new ExecutionPlanBudget(1, 0, 0, 1, 1, 10_000));

        var result = _validator.Validate(plan);

        Assert.Contains(result.Issues, issue => issue.Code == "deadline_budget_exceeded");
    }

    private static ExecutionPlan CreatePlan(
        IReadOnlyList<ExecutionPlanStep> steps,
        ExecutionPlanBudget budget) =>
        new("test@1", 1, "test.task", 1, steps, budget);

    private static ExecutionPlanStep Step(
        string id,
        ExecutionStepKind kind,
        params string[] dependencies) =>
        new(id, kind, $"test.{kind.ToString().ToLowerInvariant()}", dependencies, SideEffectClass.None, 1_000);
}
