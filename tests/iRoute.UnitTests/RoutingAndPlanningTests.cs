using System.Text.Json;
using iRoute.Contracts;
using iRoute.Core;
using iRoute.Infrastructure;
using iRoute.Runtime;

namespace iRoute.UnitTests;

public sealed class RoutingAndPlanningTests
{
    [Fact]
    public async Task SimpleTaskUsesDirectPathWithoutInvokingPlanner()
    {
        var planner = new CountingPlanner();
        var matcher = new MeasuredCapabilityMatcher(new BuiltInModelProfileRegistry());
        var router = new TaskRouter(
            new DirectPathSelector(matcher, new MeasuredEscalationPolicy()),
            planner);

        var result = await router.RouteAsync(
            EmailRequest(0.8m),
            EmailDefinition(),
            TestContext.Current.CancellationToken);

        Assert.Equal(0, planner.InvocationCount);
        Assert.Equal(RoutingPath.Direct, result.Decision.Path);
        Assert.False(result.Decision.PlannerInvoked);
        Assert.Equal(0, result.Decision.PlanningCalls);
        Assert.Equal("text.generation.small.eval-v1", result.Decision.SelectedProfileId);
        Assert.Equal(ModelTier.Small, result.Decision.SelectedModelTier);
        Assert.Equal("text.generation.small.eval-v1", Assert.Single(result.Plan.Steps).ProfileId);
    }

    [Fact]
    public async Task QualityFloorEscalatesPastIneligibleCheaperProfile()
    {
        var matcher = new MeasuredCapabilityMatcher(new BuiltInModelProfileRegistry());
        var selector = new DirectPathSelector(matcher, new MeasuredEscalationPolicy());

        var result = Assert.IsType<RoutingResult>(await selector.TrySelectAsync(
            EmailRequest(0.9m),
            EmailDefinition(),
            TestContext.Current.CancellationToken));

        Assert.Equal("text.generation.strong.eval-v1", result.Decision.SelectedProfileId);
        Assert.Equal(ModelTier.Strong, result.Decision.SelectedModelTier);
        Assert.True(result.Decision.Escalated);
        Assert.Contains("expected quality", result.Decision.EscalationReason);
        var small = Assert.Single(result.Decision.Candidates, item => item.ModelTier == ModelTier.Small);
        Assert.False(small.Eligible);
        Assert.Contains("below floor", small.Reason);
        Assert.Equal(0.84m, small.ExpectedQuality);
        Assert.Equal(0.004m, small.ExpectedCost);
        Assert.Equal(900, small.ExpectedLatencyMilliseconds);
    }

    [Fact]
    public async Task MultiCapabilityTaskCompilesOneBoundedWorkflowPlan()
    {
        var matcher = new MeasuredCapabilityMatcher(new TestModelProfileRegistry());
        var escalation = new MeasuredEscalationPolicy();
        var planner = new BoundedTaskPlanner(matcher, escalation, new ExecutionPlanValidator());

        var result = await planner.PlanAsync(
            CompositeRequest(maxDepth: 2),
            CompositeDefinition(maxDepth: 2),
            TestContext.Current.CancellationToken);

        Assert.Equal(RoutingPath.Workflow, result.Decision.Path);
        Assert.True(result.Decision.PlannerInvoked);
        Assert.Equal(1, result.Decision.PlanningCalls);
        Assert.Equal(2, result.Plan.Steps.Count);
        Assert.Equal("retrieve", result.Plan.Steps[0].Capability);
        Assert.Equal("execute", result.Plan.Steps[1].Id);
        Assert.Equal(["step-1"], result.Plan.Steps[1].DependsOn);
        Assert.Equal(1, result.Plan.Budget.MaxModelCalls);
        Assert.Equal(1, result.Plan.Budget.MaxToolCalls);
        Assert.Equal(2, result.Plan.Budget.MaxTaskDepth);
    }

    [Fact]
    public async Task PlannerFailsClosedWhenWorkflowExceedsDepthBudget()
    {
        var matcher = new MeasuredCapabilityMatcher(new TestModelProfileRegistry());
        var planner = new BoundedTaskPlanner(
            matcher,
            new MeasuredEscalationPolicy(),
            new ExecutionPlanValidator());

        var exception = await Assert.ThrowsAsync<RoutingException>(() => planner.PlanAsync(
            CompositeRequest(maxDepth: 1),
            CompositeDefinition(maxDepth: 2),
            TestContext.Current.CancellationToken));

        Assert.Equal(ErrorCodes.RoutingBudgetExceeded, exception.Code);
        Assert.Contains("requires depth 2", exception.Message);
    }

    [Fact]
    public async Task ModelProfileRegistryIsMeasuredAndCheapestFirst()
    {
        var profiles = await new BuiltInModelProfileRegistry().ListAsync(
            "text.generation",
            TestContext.Current.CancellationToken);

        Assert.Equal(2, profiles.Count);
        Assert.Equal(ModelTier.Small, profiles[0].Tier);
        Assert.True(profiles[0].EstimatedCost < profiles[1].EstimatedCost);
        Assert.All(profiles, item =>
        {
            Assert.Equal("evaluation", item.MeasurementSource);
            Assert.InRange(item.ExpectedQuality, 0m, 1m);
            Assert.InRange(item.Reliability, 0m, 1m);
            Assert.InRange(item.Availability, 0m, 1m);
        });
    }

    private static TaskRequest EmailRequest(decimal qualityFloor) => new(
        "email.draft",
        JsonSerializer.SerializeToElement(new { objective = "Explain W08 routing." }),
        Constraints: new TaskConstraints(MinimumQuality: qualityFloor, MaxModelCalls: 1));

    private static TaskDefinition EmailDefinition() => new(
        "email.draft",
        1,
        "text.generation",
        800,
        0.8m,
        true,
        SideEffectClass.None,
        "email.draft",
        AllowedCapabilities: ["text.generation"]);

    private static TaskRequest CompositeRequest(int maxDepth) => new(
        "composite.answer",
        JsonSerializer.SerializeToElement(new { question = "What changed?" }),
        Constraints: new TaskConstraints(
            MinimumQuality: 0.8m,
            MaxModelCalls: 1,
            MaxToolCalls: 1,
            MaxTaskDepth: maxDepth));

    private static TaskDefinition CompositeDefinition(int maxDepth) => new(
        "composite.answer",
        1,
        "text.generation",
        800,
        0.8m,
        true,
        SideEffectClass.None,
        "composite.answer",
        DefaultMaxModelCalls: 1,
        AllowedCapabilities: ["retrieve", "text.generation"],
        RequiredCapabilities: ["retrieve", "text.generation"],
        DefaultMaxToolCalls: 1,
        DefaultMaxTaskDepth: maxDepth);

    private sealed class CountingPlanner : IBoundedTaskPlanner
    {
        public int InvocationCount { get; private set; }

        public Task<RoutingResult> PlanAsync(
            TaskRequest request,
            TaskDefinition definition,
            CancellationToken cancellationToken)
        {
            InvocationCount++;
            throw new InvalidOperationException("A simple task invoked the planner.");
        }
    }

    private sealed class TestModelProfileRegistry : IModelProfileRegistry
    {
        public Task<IReadOnlyList<ModelProfile>> ListAsync(
            string capability,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            IReadOnlyList<ModelProfile> profiles = string.Equals(
                capability,
                "text.generation",
                StringComparison.Ordinal)
                ? [new(
                    "composite.small.eval-v1",
                    "text.generation",
                    ModelTier.Small,
                    ["composite.answer"],
                    0.9m,
                    0.005m,
                    500,
                    0.04m,
                    0.99m,
                    0.99m,
                    8_000,
                    1_000)]
                : [];
            return Task.FromResult(profiles);
        }
    }
}
