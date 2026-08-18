using System.Collections.Frozen;

namespace iRoute.Common;

public static class ExecutionStatusFacts
{
    public static FrozenSet<ExecutionStatus> TerminalStatuses { get; } = new[]
    {
        ExecutionStatus.Succeeded,
        ExecutionStatus.Failed,
        ExecutionStatus.Cancelled,
        ExecutionStatus.TimedOut
    }.ToFrozenSet();

    public static bool IsTerminal(ExecutionStatus status) => TerminalStatuses.Contains(status);
}
