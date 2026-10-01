using iRoute.Common;
using iRoute.Services;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace iRoute.Tests.Executions;

public sealed class ApprovalPolicyTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task RequesterCannotDecideTheirOwnActionEvenWithAllScopes(bool approved)
    {
        await using var fixture = await ExecutionFixture.CreateAsync();
        var pending = await fixture.Executions.ExecuteAsync(ExecutionFixture.Send(), Token);
        var error = await Assert.ThrowsAsync<ApprovalSubmissionException>(() =>
            fixture.Executions.SubmitApprovalAsync(pending.ExecutionId,
                new ApprovalDecision("execute", approved), "tenant", "requester",
                ["email:send", TaskPolicyEngine.ApprovalPermissionScope], Token));

        Assert.Equal(ErrorCodes.PermissionScopeDenied, error.Code);
        var approval = await fixture.Services.GetRequiredService<IApprovalStore>()
            .GetAsync(pending.ExecutionId, "execute", Token);
        Assert.Equal(ApprovalStatus.Pending, approval!.Status);
        var snapshot = await fixture.Services.GetRequiredService<IExecutionStore>().GetAsync(pending.ExecutionId, Token);
        Assert.Equal(ExecutionStatus.WaitingForApproval, snapshot!.Status);
        Assert.Empty(await fixture.Services.GetRequiredService<IExternalActionStore>()
            .ListUnresolvedAsync("tenant", pending.ExecutionId, Token));
    }

    [Fact]
    public async Task AnotherActorStillNeedsEveryRequiredScope()
    {
        await using var fixture = await ExecutionFixture.CreateAsync();
        var pending = await fixture.Executions.ExecuteAsync(ExecutionFixture.Send(), Token);
        var error = await Assert.ThrowsAsync<ApprovalSubmissionException>(() =>
            fixture.Executions.SubmitApprovalAsync(pending.ExecutionId,
                new ApprovalDecision("execute", true), "tenant", "reviewer",
                [TaskPolicyEngine.ApprovalPermissionScope], Token));
        Assert.Equal(ErrorCodes.PermissionScopeDenied, error.Code);
    }

    [Fact]
    public async Task CrossTenantApprovalDoesNotRevealOrChangeTheDecision()
    {
        await using var fixture = await ExecutionFixture.CreateAsync();
        var pending = await fixture.Executions.ExecuteAsync(ExecutionFixture.Send(), Token);
        var error = await Assert.ThrowsAsync<ApprovalSubmissionException>(() =>
            fixture.Executions.SubmitApprovalAsync(pending.ExecutionId,
                new ApprovalDecision("execute", true), "another-tenant", "reviewer",
                ["email:send", TaskPolicyEngine.ApprovalPermissionScope], Token));
        Assert.Equal(ErrorCodes.ApprovalNotFound, error.Code);
    }

    [Theory]
    [InlineData(null, PolicyDecisionKind.Denied)]
    [InlineData("requester", PolicyDecisionKind.Denied)]
    [InlineData("reviewer", PolicyDecisionKind.Allowed)]
    public async Task PersistedApprovalStillRequiresAnIndependentNamedActor(string? actor, PolicyDecisionKind expected)
    {
        await using var fixture = await ExecutionFixture.CreateAsync();
        var pending = await fixture.Executions.ExecuteAsync(ExecutionFixture.Send(), Token);
        var approval = await fixture.Services.GetRequiredService<IApprovalStore>().GetAsync(pending.ExecutionId, "execute", Token);
        var checkpoint = await fixture.Services.GetRequiredService<IWorkflowCheckpointStore>().GetAsync(pending.ExecutionId, Token);
        var definition = await fixture.Services.GetRequiredService<ITaskDefinitionRegistry>().FindAsync("email.send", Token);
        var policy = fixture.Services.GetRequiredService<ITaskPolicyEngine>().Evaluate(checkpoint!.Request,
            definition!, checkpoint.Plan, approval! with { Status = ApprovalStatus.Approved, DecidedByActorId = actor });
        Assert.Equal(expected, policy.Decision);
    }
}
