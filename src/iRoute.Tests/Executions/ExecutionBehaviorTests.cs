using System.Text.Json;
using iRoute.Common;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace iRoute.Tests.Executions;

public sealed class ExecutionBehaviorTests
{
    [Fact]
    public async Task InlineExecutionValidatesAndStoresItsArtifact()
    {
        await using var fixture = await ExecutionFixture.CreateAsync();
        var result = await fixture.Executions.ExecuteAsync(ExecutionFixture.Draft(), TestContext.Current.CancellationToken);
        Assert.Equal(ExecutionStatus.Succeeded, result.Status);
        Assert.NotNull(result.Outcome);
        Assert.True(result.Outcome.Validation?.Passed);
        var artifact = Assert.Single(result.Outcome!.Artifacts);
        Assert.NotNull(await fixture.Services.GetRequiredService<IArtifactStore>()
            .GetAsync("tenant", artifact.ArtifactId, TestContext.Current.CancellationToken));
        Assert.Null(await fixture.Services.GetRequiredService<IArtifactStore>()
            .GetAsync("other-tenant", artifact.ArtifactId, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task QueuedExecutionSurvivesSubmissionAndRunsUnderALease()
    {
        await using var fixture = await ExecutionFixture.CreateAsync();
        var accepted = await fixture.Executions.SubmitAsync(ExecutionFixture.Draft(), TestContext.Current.CancellationToken);
        Assert.Equal(ExecutionStatus.Queued, accepted.Status);
        var completed = await fixture.ProcessAsync(accepted.ExecutionId);
        Assert.Equal(ExecutionStatus.Succeeded, completed.Status);
        Assert.NotNull(completed.Outcome);
        Assert.True(completed.Outcome.Validation?.Passed);
        var events = new List<ExecutionEvent>();
        await foreach (var item in fixture.Services.GetRequiredService<IExecutionStore>()
            .ReadEventsAsync(completed.ExecutionId, 0, TestContext.Current.CancellationToken))
        {
            events.Add(item);
        }

        Assert.Contains(events, item => item.Type == ExecutionEventTypes.Completed);
        Assert.Equal(events.Count, events.Select(item => item.Sequence).Distinct().Count());
    }

    [Fact]
    public async Task IdempotentReplayReturnsTheOriginalExecution()
    {
        await using var fixture = await ExecutionFixture.CreateAsync();
        var request = ExecutionFixture.Draft();
        var first = await fixture.Executions.SubmitAsync(request, TestContext.Current.CancellationToken);
        var replay = await fixture.Executions.SubmitAsync(request, TestContext.Current.CancellationToken);
        Assert.Equal(first.ExecutionId, replay.ExecutionId);
    }

    [Fact]
    public async Task IdempotencyKeyCannotBeReusedWithDifferentInput()
    {
        await using var fixture = await ExecutionFixture.CreateAsync();
        var request = ExecutionFixture.Draft();
        await fixture.Executions.SubmitAsync(request, TestContext.Current.CancellationToken);
        await Assert.ThrowsAsync<IdempotencyKeyReusedException>(() => fixture.Executions.SubmitAsync(
            request with { Input = JsonSerializer.SerializeToElement(new { objective = "Different input" }) }, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task IdempotencyKeysAreIsolatedByTenant()
    {
        await using var fixture = await ExecutionFixture.CreateAsync();
        var first = await fixture.Executions.SubmitAsync(ExecutionFixture.Draft(tenant: "first"), TestContext.Current.CancellationToken);
        var second = await fixture.Executions.SubmitAsync(ExecutionFixture.Draft(tenant: "second"), TestContext.Current.CancellationToken);
        Assert.NotEqual(first.ExecutionId, second.ExecutionId);
    }

    [Theory]
    [InlineData("unknown.task", ErrorCodes.UnknownTaskType)]
    [InlineData("email.draft", ErrorCodes.ModelBudgetExhausted)]
    public async Task InvalidExecutionFailsWithAStableProblem(string taskType, string errorCode)
    {
        await using var fixture = await ExecutionFixture.CreateAsync();
        var request = ExecutionFixture.Draft() with
        {
            TaskType = taskType,
            Constraints = new TaskConstraints(MaxModelCalls: 0)
        };
        var result = await fixture.Executions.ExecuteAsync(request, TestContext.Current.CancellationToken);
        Assert.Equal(ExecutionStatus.Failed, result.Status);
        Assert.Equal(errorCode, result.Error?.Code);
    }

    [Fact]
    public async Task ExternalWritesRequirePermissionsBeforeApproval()
    {
        await using var fixture = await ExecutionFixture.CreateAsync();
        var result = await fixture.Executions.SubmitAsync(ExecutionFixture.Send() with { PermissionScopes = [] }, TestContext.Current.CancellationToken);
        Assert.Equal(ExecutionStatus.Failed, result.Status);
        Assert.Equal(ErrorCodes.PermissionScopeDenied, result.Error?.Code);
    }

    [Fact]
    public async Task SeparateAuthorizedActorCanApproveAndResumeAnExternalAction()
    {
        await using var fixture = await ExecutionFixture.CreateAsync();
        var waiting = await fixture.Executions.SubmitAsync(ExecutionFixture.Send(), TestContext.Current.CancellationToken);
        Assert.Equal(ExecutionStatus.WaitingForApproval, waiting.Status);
        var approved = await fixture.Executions.SubmitApprovalAsync(
            waiting.ExecutionId, new ApprovalDecision("execute", true), "tenant", "reviewer",
            ["email:send", "approval:grant"], TestContext.Current.CancellationToken);
        Assert.Equal(ApprovalStatus.Approved, approved.Approval.Status);
        Assert.Equal(ExecutionStatus.Succeeded, approved.Execution.Status);
    }
}
