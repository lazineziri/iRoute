using iRoute.Contracts;
using iRoute.Core;

namespace iRoute.UnitTests;

public sealed class ExecutionStateMachineTests
{
    [Fact]
    public void CompletedExecutionCannotRestart()
    {
        Assert.False(ExecutionStateMachine.CanTransition(ExecutionStatus.Succeeded, ExecutionStatus.Running));
    }

    [Fact]
    public void AcceptedExecutionCanEnterResolution()
    {
        Assert.True(ExecutionStateMachine.CanTransition(ExecutionStatus.Accepted, ExecutionStatus.Resolving));
    }

    [Fact]
    public void PlannedAndApprovedExecutionsCanEnterTheDurableQueue()
    {
        Assert.True(ExecutionStateMachine.CanTransition(ExecutionStatus.Planning, ExecutionStatus.Queued));
        Assert.True(ExecutionStateMachine.CanTransition(
            ExecutionStatus.WaitingForApproval,
            ExecutionStatus.Queued));
        Assert.True(ExecutionStateMachine.CanTransition(ExecutionStatus.Queued, ExecutionStatus.Running));
    }

    [Fact]
    public void AcceptedExecutionCanFailBeforeResolutionStarts()
    {
        Assert.True(ExecutionStateMachine.CanTransition(ExecutionStatus.Accepted, ExecutionStatus.Failed));
    }

    [Theory]
    [InlineData(ExecutionStatus.Accepted)]
    [InlineData(ExecutionStatus.Resolving)]
    [InlineData(ExecutionStatus.Planning)]
    [InlineData(ExecutionStatus.Queued)]
    [InlineData(ExecutionStatus.WaitingForApproval)]
    [InlineData(ExecutionStatus.Running)]
    [InlineData(ExecutionStatus.Validating)]
    [InlineData(ExecutionStatus.Materializing)]
    public void EveryNonTerminalStatusCanReachATerminalFailure(ExecutionStatus from)
    {
        Assert.True(
            ExecutionStateMachine.CanTransition(from, ExecutionStatus.Failed),
            $"{from} cannot reach Failed, so an error raised in that phase cannot be recorded.");
        Assert.True(
            ExecutionStateMachine.CanTransition(from, ExecutionStatus.Cancelled),
            $"{from} cannot reach Cancelled.");
        Assert.True(
            ExecutionStateMachine.CanTransition(from, ExecutionStatus.TimedOut),
            $"{from} cannot reach TimedOut, so a deadline in that phase cannot be recorded.");
    }
}
