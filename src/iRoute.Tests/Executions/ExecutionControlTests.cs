using iRoute.Common;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace iRoute.Tests.Executions;

public sealed class ExecutionControlTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task CancellingQueuedWorkPersistsTerminalEventsAndPreventsClaiming()
    {
        await using var fixture = await ExecutionFixture.CreateAsync();
        var queued = await fixture.Executions.SubmitAsync(ExecutionFixture.Draft(), Token);
        var cancelled = await fixture.Executions.CancelAsync(queued.ExecutionId, "tenant", Token);
        Assert.Equal(ExecutionStatus.Cancelled, cancelled?.Status);
        var work = fixture.Services.GetRequiredService<IExecutionWorkStore>();
        Assert.Equal(ExecutionWorkState.Cancelled, (await work.GetAsync(queued.ExecutionId, Token))?.State);
        Assert.Null(await work.TryClaimAsync("worker", DateTimeOffset.UtcNow, TimeSpan.FromMinutes(1), Token));
        var events = await ReadEventsAsync(fixture, queued.ExecutionId);
        Assert.Contains(events, item => item.Type == ExecutionEventTypes.CancellationRequested);
        Assert.Contains(events, item => item.Type == ExecutionEventTypes.Failed);
    }

    [Fact]
    public async Task CancellingWaitingApprovalPreventsLaterApproval()
    {
        await using var fixture = await ExecutionFixture.CreateAsync();
        var waiting = await fixture.Executions.SubmitAsync(ExecutionFixture.Send(), Token);
        var cancelled = await fixture.Executions.CancelAsync(waiting.ExecutionId, "tenant", Token);
        Assert.Equal(ExecutionStatus.Cancelled, cancelled?.Status);
        var error = await Assert.ThrowsAsync<ApprovalSubmissionException>(() =>
            fixture.Executions.SubmitApprovalAsync(waiting.ExecutionId, new ApprovalDecision("execute", true),
                "tenant", "reviewer", ["email:send", "approval:grant"], Token));
        Assert.Equal(ErrorCodes.ApprovalAlreadyDecided, error.Code);
    }

    [Fact]
    public async Task CancellationCannotAffectAnotherTenant()
    {
        await using var fixture = await ExecutionFixture.CreateAsync();
        var queued = await fixture.Executions.SubmitAsync(ExecutionFixture.Draft(), Token);
        Assert.Null(await fixture.Executions.CancelAsync(queued.ExecutionId, "other-tenant", Token));
        var saved = await fixture.Services.GetRequiredService<IExecutionStore>().GetAsync(queued.ExecutionId, Token);
        Assert.Equal(ExecutionStatus.Queued, saved?.Status);
        Assert.Null(saved?.CancellationRequestedAt);
    }

    [Fact]
    public async Task CancellationCannotOverwriteASuccessfulOutcome()
    {
        await using var fixture = await ExecutionFixture.CreateAsync();
        var completed = await fixture.Executions.ExecuteAsync(ExecutionFixture.Draft(), Token);
        var error = await Assert.ThrowsAsync<ExecutionCommandException>(() =>
            fixture.Executions.CancelAsync(completed.ExecutionId, "tenant", Token));
        Assert.Equal(ErrorCodes.ExecutionAlreadyTerminal, error.Code);
        var saved = await fixture.Services.GetRequiredService<IExecutionStore>().GetAsync(completed.ExecutionId, Token);
        Assert.Equal(ExecutionStatus.Succeeded, saved?.Status);
        Assert.Equal(completed.Outcome?.Output.GetRawText(), saved?.Outcome?.Output.GetRawText());
    }

    [Fact]
    public async Task RepeatedRunningCancellationKeepsTheOriginalRequestAndOneAuditEvent()
    {
        await using var fixture = await ExecutionFixture.CreateAsync();
        var now = DateTimeOffset.UtcNow;
        var running = new ExecutionSnapshot(Guid.CreateVersion7(), "email.draft", ExecutionStatus.Running, now, now,
            TenantId: "tenant", ActorId: "requester");
        await fixture.Services.GetRequiredService<IExecutionStore>().CreateAsync(running, null, null, Token);
        var first = await fixture.Executions.CancelAsync(running.ExecutionId, "tenant", Token);
        var repeated = await fixture.Executions.CancelAsync(running.ExecutionId, "tenant", Token);
        Assert.Equal(ExecutionStatus.Running, repeated?.Status);
        Assert.NotNull(first?.CancellationRequestedAt);
        Assert.Equal(first?.CancellationRequestedAt, repeated?.CancellationRequestedAt);
        Assert.Single((await ReadEventsAsync(fixture, running.ExecutionId))
, item => item.Type == ExecutionEventTypes.CancellationRequested);
    }

    [Theory]
    [InlineData("succeeded", ExternalActionStatus.Succeeded)]
    [InlineData("failed", ExternalActionStatus.Failed)]
    public async Task AuthorizedReconciliationRecordsTheResultAndAuditEvent(string outcome, ExternalActionStatus expected)
    {
        await using var fixture = await ExecutionFixture.CreateAsync();
        var execution = await SeedUnresolvedAsync(fixture);
        var result = await fixture.Executions.ReconcileActionAsync(execution.ExecutionId, "send",
            new ExternalActionReconciliation(outcome), "tenant", "reviewer", ["approval:grant"], Token);
        Assert.Equal(expected.ToString(), result?.Status);
        Assert.Empty((await fixture.Executions.ListUnresolvedActionsAsync(
            execution.ExecutionId, "tenant", ["approval:grant"], Token))!);
        Assert.Contains(await ReadEventsAsync(fixture, execution.ExecutionId),
            item => item.Type == ExecutionEventTypes.ExternalActionReconciled);
    }

    [Fact]
    public async Task ReconciliationEnforcesTenantAndPermissionBoundaries()
    {
        await using var fixture = await ExecutionFixture.CreateAsync();
        var execution = await SeedUnresolvedAsync(fixture);
        Assert.Null(await fixture.Executions.ReconcileActionAsync(execution.ExecutionId, "send",
            new ExternalActionReconciliation("succeeded"), "other-tenant", "reviewer", ["approval:grant"], Token));
        var error = await Assert.ThrowsAsync<ExecutionCommandException>(() =>
            fixture.Executions.ReconcileActionAsync(execution.ExecutionId, "send",
                new ExternalActionReconciliation("succeeded"), "tenant", "requester", [], Token));
        Assert.Equal(ErrorCodes.PermissionScopeDenied, error.Code);
        Assert.Single((await fixture.Executions.ListUnresolvedActionsAsync(
            execution.ExecutionId, "tenant", ["approval:grant"], Token))!);
    }

    [Fact]
    public async Task InvalidReconciliationDoesNotMutateTheAction()
    {
        await using var fixture = await ExecutionFixture.CreateAsync();
        var execution = await SeedUnresolvedAsync(fixture);
        var error = await Assert.ThrowsAsync<ExecutionCommandException>(() =>
            fixture.Executions.ReconcileActionAsync(execution.ExecutionId, "send",
                new ExternalActionReconciliation("unknown"), "tenant", "reviewer", ["approval:grant"], Token));
        Assert.Equal(ErrorCodes.InvalidTaskRequest, error.Code);
        Assert.Single((await fixture.Executions.ListUnresolvedActionsAsync(
            execution.ExecutionId, "tenant", ["approval:grant"], Token))!);
    }

    private static async Task<ExecutionSnapshot> SeedUnresolvedAsync(ExecutionFixture fixture)
    {
        var now = DateTimeOffset.UtcNow;
        var execution = new ExecutionSnapshot(Guid.CreateVersion7(), "email.send", ExecutionStatus.Failed, now, now,
            TenantId: "tenant", ActorId: "requester");
        await fixture.Services.GetRequiredService<IExecutionStore>().CreateAsync(execution, null, null, Token);
        await fixture.Services.GetRequiredService<IExternalActionStore>().ReserveAsync(new ExternalActionRecord(
            execution.ExecutionId, "tenant", "send", "email.send", "reference", "input", ExternalActionStatus.Running,
            now, now), Token);
        return execution;
    }

    private static async Task<List<ExecutionEvent>> ReadEventsAsync(ExecutionFixture fixture, Guid executionId)
    {
        var events = new List<ExecutionEvent>();
        await foreach (var item in fixture.Services.GetRequiredService<IExecutionStore>().ReadEventsAsync(executionId, 0, Token))
        {
            events.Add(item);
        }

        return events;
    }
}
