using iRoute.Common;

namespace iRoute.Services;

public sealed partial class ExecutionService
{
    public async Task<ExecutionSnapshot> ProcessQueuedAsync(
        Guid executionId,
        CancellationToken cancellationToken)
    {
        var snapshot = await store.GetAsync(executionId, cancellationToken)
            ?? throw new KeyNotFoundException($"Execution '{executionId}' was not found.");
        if (IsTerminal(snapshot.Status))
        {
            return snapshot;
        }

        if (snapshot.Status is not (ExecutionStatus.Queued or ExecutionStatus.Running))
        {
            throw new InvalidOperationException(
                $"Execution '{executionId}' cannot be processed from {snapshot.Status}.");
        }

        var checkpoint = await checkpoints.GetAsync(executionId, cancellationToken)
            ?? throw new InvalidOperationException(
                $"Queued execution '{executionId}' has no durable workflow checkpoint.");
        var definition = await taskDefinitions.FindAsync(checkpoint.Request.TaskType, cancellationToken)
            ?? throw new InvalidOperationException(
                $"No active task definition exists for '{checkpoint.Request.TaskType}'.");
        using var trace = _telemetry.StartExecution(
            snapshot,
            checkpoint.Request.PermissionScopes ?? [],
            "worker");
        var remainingDeadline = await RemainingWorkerDeadlineAsync(
            executionId,
            checkpoint.Plan.Budget.DeadlineMilliseconds,
            cancellationToken);
        if (remainingDeadline <= TimeSpan.Zero)
        {
            await CancelCheckpointAsync(executionId, CancellationToken.None);
            return await TerminalAsync(
                snapshot,
                ExecutionStatus.TimedOut,
                new Problem(
                    ErrorCodes.ExecutionTimedOut,
                    "Execution timed out",
                    "The execution exceeded its durable queue deadline.",
                    true),
                CancellationToken.None);
        }

        using var deadline = new CancellationTokenSource(remainingDeadline);
        using var execution = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            deadline.Token);
        try
        {
            if (snapshot.CancellationRequestedAt is not null)
            {
                await CancelCheckpointAsync(executionId, CancellationToken.None);
                return await TerminalAsync(
                    snapshot,
                    ExecutionStatus.Cancelled,
                    new Problem(
                        ErrorCodes.ExecutionCancelled,
                        "Execution cancelled",
                        "The execution was cancelled before a worker started it."),
                    CancellationToken.None);
            }

            return await RunPlanAsync(
                snapshot,
                checkpoint.Request,
                definition,
                checkpoint.Plan,
                checkpoint.Routing,
                true,
                execution.Token);
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested)
        {
            await CancelCheckpointAsync(executionId, CancellationToken.None);
            return await TerminalAsync(
                snapshot,
                ExecutionStatus.TimedOut,
                new Problem(
                    ErrorCodes.ExecutionTimedOut,
                    "Execution timed out",
                    "The execution exceeded its worker deadline.",
                    true),
                CancellationToken.None);
        }
        catch (OperationCanceledException)
        {
            var latest = await store.GetAsync(executionId, CancellationToken.None) ?? snapshot;
            if (latest.CancellationRequestedAt is null)
            {
                throw;
            }

            await CancelCheckpointAsync(executionId, CancellationToken.None);
            return await TerminalAsync(
                latest,
                ExecutionStatus.Cancelled,
                new Problem(
                    ErrorCodes.ExecutionCancelled,
                    "Execution cancelled",
                    "The execution was cancelled while running."),
                CancellationToken.None);
        }
        catch (Exception exception) when (IsExecutionFailure(exception))
        {
            return await HandleResumedFailureAsync(snapshot, exception, false);
        }
    }

}
