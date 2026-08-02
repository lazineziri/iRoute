using iRoute.Contracts;

namespace iRoute.Core;

public static class ExecutionStateMachine
{
    private static readonly Dictionary<ExecutionStatus, ExecutionStatus[]> Allowed =
        new Dictionary<ExecutionStatus, ExecutionStatus[]>
        {
            [ExecutionStatus.Accepted] = [ExecutionStatus.Resolving, ExecutionStatus.Failed, ExecutionStatus.Cancelled, ExecutionStatus.TimedOut],
            [ExecutionStatus.Resolving] = [ExecutionStatus.Planning, ExecutionStatus.Validating, ExecutionStatus.Failed, ExecutionStatus.Cancelled, ExecutionStatus.TimedOut],
            [ExecutionStatus.Planning] = [ExecutionStatus.Queued, ExecutionStatus.WaitingForApproval, ExecutionStatus.Running, ExecutionStatus.Failed, ExecutionStatus.Cancelled, ExecutionStatus.TimedOut],
            [ExecutionStatus.Queued] = [ExecutionStatus.Running, ExecutionStatus.Cancelled, ExecutionStatus.Failed, ExecutionStatus.TimedOut],
            [ExecutionStatus.WaitingForApproval] = [ExecutionStatus.Queued, ExecutionStatus.Running, ExecutionStatus.Cancelled, ExecutionStatus.Failed, ExecutionStatus.TimedOut],
            [ExecutionStatus.Running] = [ExecutionStatus.Validating, ExecutionStatus.Compensating, ExecutionStatus.Failed, ExecutionStatus.Cancelled, ExecutionStatus.TimedOut],
            [ExecutionStatus.Validating] = [ExecutionStatus.Materializing, ExecutionStatus.Compensating, ExecutionStatus.Failed, ExecutionStatus.Cancelled, ExecutionStatus.TimedOut],
            [ExecutionStatus.Materializing] = [ExecutionStatus.Succeeded, ExecutionStatus.Compensating, ExecutionStatus.Failed, ExecutionStatus.Cancelled, ExecutionStatus.TimedOut],
            [ExecutionStatus.Compensating] = [ExecutionStatus.Failed, ExecutionStatus.Cancelled],
            [ExecutionStatus.Succeeded] = [],
            [ExecutionStatus.Failed] = [],
            [ExecutionStatus.Cancelled] = [],
            [ExecutionStatus.TimedOut] = []
        };

    public static bool CanTransition(ExecutionStatus from, ExecutionStatus to) =>
        Allowed.TryGetValue(from, out var targets) && targets.Contains(to);

    public static void EnsureCanTransition(ExecutionStatus from, ExecutionStatus to)
    {
        if (!CanTransition(from, to))
        {
            throw new InvalidOperationException($"Execution cannot transition from {from} to {to}.");
        }
    }
}
