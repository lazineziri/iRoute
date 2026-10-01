using System.Text.Json;
using iRoute.Common;
using iRoute.Tests.Executions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace iRoute.Tests.Optimization;

public sealed class ResultReuseTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private static readonly string[] ChangedFacts = ["Changed fact."];

    [Fact]
    public async Task ANewExecutionReusesAnArtifactWithoutAnotherModelCall()
    {
        var gateway = new OptimizationGateway();
        await using var fixture = await ExecutionFixture.CreateAsync(gateway);
        var first = await fixture.Executions.ExecuteAsync(ExecutionFixture.Draft("cold"), Token);
        var warm = await fixture.Executions.ExecuteAsync(ExecutionFixture.Draft("warm"), Token);
        Assert.Equal(ExecutionStatus.Succeeded, first.Status);
        Assert.Equal(ExecutionStatus.Succeeded, warm.Status);
        Assert.NotEqual(first.ExecutionId, warm.ExecutionId);
        Assert.Single(gateway.Requests);
        Assert.Equal(ResolutionLevel.ExactArtifact, warm.Outcome!.ResolutionLevel);
        Assert.Equal(0, warm.Outcome.Usage.InputTokens + warm.Outcome.Usage.OutputTokens);
        Assert.Equal(0, warm.Outcome.Usage.ModelCalls);
        Assert.True(JsonElement.DeepEquals(first.Outcome!.Output, warm.Outcome.Output));
        Assert.Equal(first.Outcome.Confidence, warm.Outcome.Confidence);
        Assert.Equal(12, first.Outcome.Usage.CachedInputTokens);
        Assert.Equal(4, first.Outcome.Usage.ReasoningTokens);
    }

    [Theory]
    [InlineData("input")]
    [InlineData("tenant")]
    [InlineData("project")]
    [InlineData("output-limit")]
    public async Task ChangedInputsScopesAndOutputPolicyCannotReuseAnIncompatibleResult(string change)
    {
        var gateway = new OptimizationGateway();
        await using var fixture = await ExecutionFixture.CreateAsync(gateway);
        var first = ExecutionFixture.Draft("cold");
        await fixture.Executions.ExecuteAsync(first, Token);
        var next = first with { IdempotencyKey = "new" };
        next = change switch
        {
            "input" => next with { Input = JsonSerializer.SerializeToElement(new { objective = "Changed", facts = ChangedFacts }) },
            "tenant" => next with { TenantId = "another-tenant" },
            "project" => next with { ProjectId = "another-project" },
            _ => next with { Constraints = new TaskConstraints(MaxOutputTokens: 10) }
        };
        await fixture.Executions.ExecuteAsync(next, Token);
        Assert.Equal(2, gateway.Requests.Count);
    }

    [Fact]
    public async Task DependencyInvalidationForcesFreshInference()
    {
        var gateway = new OptimizationGateway();
        await using var fixture = await ExecutionFixture.CreateAsync(gateway);
        var first = await fixture.Executions.ExecuteAsync(ExecutionFixture.Draft("cold"), Token);
        var reference = Assert.Single(first.Outcome!.Artifacts);
        var store = fixture.Services.GetRequiredService<IArtifactStore>();
        var artifact = (await store.GetAsync("tenant", reference.ArtifactId, Token))!;
        var dependency = artifact.EffectiveDependencies[0];
        var invalidated = await store.InvalidateByDependencyAsync(new DependencyChange("tenant", dependency.Kind,
            dependency.Reference, dependency.ContentHash, true, "Benchmark source changed", DateTimeOffset.UtcNow), Token);
        Assert.Contains(reference.ArtifactId, invalidated.ArtifactIds);
        await fixture.Executions.ExecuteAsync(ExecutionFixture.Draft("new"), Token);
        Assert.Equal(2, gateway.Requests.Count);
    }

    [Fact]
    public async Task ANoModelBudgetCanStillUseAnOtherwiseCompatibleArtifact()
    {
        var gateway = new OptimizationGateway();
        await using var fixture = await ExecutionFixture.CreateAsync(gateway);
        await fixture.Executions.ExecuteAsync(ExecutionFixture.Draft("cold"), Token);
        var warm = await fixture.Executions.ExecuteAsync(ExecutionFixture.Draft("warm") with { Constraints = new TaskConstraints(MaxModelCalls: 0) }, Token);
        Assert.Equal(ExecutionStatus.Succeeded, warm.Status);
        Assert.Single(gateway.Requests);
        Assert.Equal(0, warm.Outcome!.Usage.ModelCalls);
    }

    [Fact]
    public async Task IdempotentReplayCannotSilentlyChangeConstraints()
    {
        await using var fixture = await ExecutionFixture.CreateAsync();
        var request = ExecutionFixture.Draft();
        await fixture.Executions.ExecuteAsync(request, Token);
        await Assert.ThrowsAsync<IdempotencyKeyReusedException>(() => fixture.Executions.ExecuteAsync(
            request with { Constraints = new TaskConstraints(MaxOutputTokens: 10) }, Token));
    }

    [Fact]
    public async Task LegacyArtifactsWithoutQualityMetadataCannotInventPerfectConfidence()
    {
        var gateway = new OptimizationGateway();
        await using var fixture = await ExecutionFixture.CreateAsync(gateway);
        var first = await fixture.Executions.ExecuteAsync(ExecutionFixture.Draft("cold"), Token);
        var artifact = (await fixture.Services.GetRequiredService<IArtifactStore>().GetAsync("tenant",
            first.Outcome!.Artifacts[0].ArtifactId, Token))!;
        Assert.Equal(first.Outcome.Confidence, artifact.Confidence);
        var contexts = fixture.Services.GetRequiredService<Microsoft.EntityFrameworkCore.IDbContextFactory<iRoute.Data.IRouteDbContext>>();
        await using var context = await contexts.CreateDbContextAsync(Token);
        var entity = await context.Artifacts.FindAsync([artifact.ArtifactId], Token);
        entity!.Confidence = null;
        await context.SaveChangesAsync(Token);
        var next = await fixture.Executions.ExecuteAsync(ExecutionFixture.Draft("new"), Token);
        Assert.Equal(2, gateway.Requests.Count);
        Assert.Equal(0.9m, next.Outcome!.Confidence);
        var warm = await fixture.Executions.ExecuteAsync(ExecutionFixture.Draft("warm-after-upgrade"), Token);
        Assert.Equal(2, gateway.Requests.Count);
        Assert.Equal(ResolutionLevel.ExactArtifact, warm.Outcome!.ResolutionLevel);
        Assert.Equal(0.9m, warm.Outcome.Confidence);
    }

    [Fact]
    public async Task ExpiredArtifactsCannotSuppressFreshInference()
    {
        var clock = new MutableClock();
        var gateway = new OptimizationGateway();
        await using var fixture = await ExecutionFixture.CreateAsync(gateway, clock: clock);
        var first = await fixture.Executions.ExecuteAsync(ExecutionFixture.Draft("cold"), Token);
        var artifact = (await fixture.Services.GetRequiredService<IArtifactStore>().GetAsync("tenant", first.Outcome!.Artifacts[0].ArtifactId, Token))!;
        Assert.NotNull(artifact.ExpiresAt);
        clock.Now = artifact.ExpiresAt!.Value.AddSeconds(1);
        await fixture.Executions.ExecuteAsync(ExecutionFixture.Draft("new"), Token);
        Assert.Equal(2, gateway.Requests.Count);
    }

    private sealed class MutableClock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => Now;
    }
}
