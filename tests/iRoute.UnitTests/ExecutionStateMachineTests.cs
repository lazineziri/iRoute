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
}
