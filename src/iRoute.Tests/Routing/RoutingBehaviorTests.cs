using System.Text.Json;
using iRoute.Common;
using iRoute.Tests.Executions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace iRoute.Tests.Routing;

public sealed class RoutingBehaviorTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private static readonly string[] DraftTypes = ["email.draft"];
    private static readonly string[] WorkflowCapabilities = ["text.summarization", "text.generation"];

    [Fact]
    public async Task RequestedLimitsCannotRaiseTaskDefinitionBudgets()
    {
        var gateway = new RoutingGateway();
        await using var fixture = await ExecutionFixture.CreateAsync(gateway);
        var definition = (await fixture.Services.GetRequiredService<ITaskDefinitionRegistry>().FindAsync("email.draft", Token))!;
        var result = await fixture.Executions.ExecuteAsync(ExecutionFixture.Draft() with
        { Constraints = new TaskConstraints(MaxInputTokens: 100000, MaxOutputTokens: 100000) }, Token);
        Assert.Equal(ExecutionStatus.Succeeded, result.Status);
        Assert.Equal(definition.DefaultMaxInputTokens, result.Outcome!.Context!.BudgetTokens);
        Assert.Equal(definition.DefaultMaxOutputTokens, gateway.LastRequest!.MaxOutputTokens);
    }

    [Theory]
    [InlineData(0.8, "cheap", false)]
    [InlineData(0.9, "strong", true)]
    public async Task FullPipelineSelectsTheCheapestEligibleQualityTier(double floor, string expected, bool escalated)
    {
        await using var fixture = await ExecutionFixture.CreateAsync(new RoutingGateway(), configure: services =>
            services.AddSingleton<IModelProfileRegistry>(new Profiles()));
        var result = await fixture.Executions.ExecuteAsync(ExecutionFixture.Draft() with
        { Constraints = new TaskConstraints(MinimumQuality: (decimal)floor) }, Token);
        Assert.Equal(ExecutionStatus.Succeeded, result.Status);
        Assert.Equal(expected, result.Outcome!.Routing!.SelectedProfileId);
        Assert.Equal(escalated, result.Outcome.Routing.Escalated);
        Assert.False(result.Outcome.Routing.PlannerInvoked);
        Assert.Equal(1, result.Outcome.Usage.ModelCalls);
    }

    [Theory]
    [InlineData("quality")]
    [InlineData("cost")]
    [InlineData("tokens")]
    [InlineData("measurement")]
    public async Task IneligibleProfilesFailBeforeCallingAProvider(string restriction)
    {
        var gateway = new RoutingGateway();
        await using var fixture = await ExecutionFixture.CreateAsync(gateway, configure: services =>
            services.AddSingleton<IModelProfileRegistry>(new Profiles(restriction == "measurement", restriction == "tokens")));
        var constraints = restriction switch
        {
            "quality" => new TaskConstraints(MinimumQuality: 0.99m),
            "cost" => new TaskConstraints(MaxCost: 0.0001m),
            "tokens" => new TaskConstraints(MaxInputTokens: 100000),
            _ => new TaskConstraints()
        };
        var result = await fixture.Executions.ExecuteAsync(ExecutionFixture.Draft() with { Constraints = constraints }, Token);
        Assert.Equal(ExecutionStatus.Failed, result.Status);
        Assert.Equal(0, gateway.Calls);
        Assert.Equal(ErrorCodes.RoutingNoEligibleCapability, result.Error!.Code);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BoundedMultiStepPlansAggregateUsageAndStopOnFailure(bool failFirst)
    {
        var gateway = new RoutingGateway(failFirst);
        await using var fixture = await ExecutionFixture.CreateAsync(gateway, configure: services =>
        {
            services.AddSingleton<IModelProfileRegistry>(new Profiles());
            services.AddSingleton<ITaskDefinitionRegistry>(new WorkflowDefinition());
        });
        var result = await fixture.Executions.ExecuteAsync(ExecutionFixture.Draft() with
        { Constraints = new TaskConstraints(MaxModelCalls: 2, MaxTaskDepth: 2) }, Token);
        Assert.Equal(failFirst ? ExecutionStatus.Failed : ExecutionStatus.Succeeded, result.Status);
        Assert.Equal(failFirst ? 1 : 2, gateway.Calls);
        if (!failFirst)
        {
            Assert.True(result.Outcome!.Routing!.PlannerInvoked);
            Assert.Equal(1, result.Outcome.Routing.PlanningCalls);
            Assert.Equal(2, result.Outcome.Usage.ModelCalls);
            Assert.Equal(200, result.Outcome.Usage.InputTokens);
            Assert.Equal(20, result.Outcome.Usage.CachedInputTokens);
            Assert.Equal(8, result.Outcome.Usage.ReasoningTokens);
        }
        else Assert.Null(result.Outcome);
    }

    [Fact]
    public async Task InsufficientWorkflowBudgetStopsBeforeExecution()
    {
        var gateway = new RoutingGateway();
        await using var fixture = await ExecutionFixture.CreateAsync(gateway, configure: services =>
        {
            services.AddSingleton<IModelProfileRegistry>(new Profiles());
            services.AddSingleton<ITaskDefinitionRegistry>(new WorkflowDefinition());
        });
        var result = await fixture.Executions.ExecuteAsync(ExecutionFixture.Draft() with
        { Constraints = new TaskConstraints(MaxModelCalls: 1, MaxTaskDepth: 2) }, Token);
        Assert.Equal(ExecutionStatus.Failed, result.Status);
        Assert.Equal(0, gateway.Calls);
    }

    private sealed class Profiles(bool invalidMeasurement = false, bool smallCapacity = false) : IModelProfileRegistry
    {
        public Task<IReadOnlyList<ModelProfile>> ListAsync(string capability, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<ModelProfile>>([
                Profile("cheap", capability, ModelTier.Small, 0.85m, 0.001m),
                Profile("strong", capability, ModelTier.Strong, 0.97m, 0.02m)]);

        private ModelProfile Profile(string id, string capability, ModelTier tier, decimal quality, decimal cost) => new(
            id, capability, tier, DraftTypes, quality, cost, 100, 0.01m, 1, 1, smallCapacity ? 8 : 32000, 8000,
            MeasurementSource: invalidMeasurement ? ModelProfileSource.Measured : ModelProfileSource.Synthetic);
    }

    private sealed class WorkflowDefinition : ITaskDefinitionRegistry
    {
        public Task<TaskDefinition?> FindAsync(string taskType, CancellationToken cancellationToken) =>
            Task.FromResult<TaskDefinition?>(new("email.draft", 1, "text.generation", 1000, 0.8m, false,
                SideEffectClass.None, "email-draft", DefaultMaxModelCalls: 2, AllowedCapabilities: WorkflowCapabilities,
                RequiredCapabilities: WorkflowCapabilities, DefaultMaxTaskDepth: 2));
    }

    private sealed class RoutingGateway(bool failFirst = false) : IModelGateway
    {
        public int Calls { get; private set; }
        public ModelGatewayRequest? LastRequest { get; private set; }
        public Task<ModelGatewayResult> ExecuteAsync(ModelGatewayRequest request, CancellationToken cancellationToken)
        {
            Calls++;
            LastRequest = request;
            if (failFirst && Calls == 1) throw new ModelGatewayException(ErrorCodes.ModelGatewayUnavailable,
                "Controlled failure", false, failureKind: ModelGatewayFailureKind.Unavailable);
            return Task.FromResult(new ModelGatewayResult(JsonSerializer.SerializeToElement(new
            { subject = "Review", body = "Review the implementation.", tone = "professional" }),
                new UsageSummary(100, 20, ModelCalls: 1, CachedInputTokens: 10, ReasoningTokens: 4), 0.97m, []));
        }
    }
}
