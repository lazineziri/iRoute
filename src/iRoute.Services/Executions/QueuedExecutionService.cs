using iRoute.Common;
using static iRoute.Services.ExecutionValidation;

namespace iRoute.Services;

public sealed class QueuedExecutionService(
    IExecutionStore store,
    IWorkflowCheckpointStore checkpoints,
    ITaskDefinitionRegistry taskDefinitions,
    TimeProvider clock,
    IExecutionTelemetry telemetry,
    ExecutionPersistenceService persistence,
    PlanExecutionService plans)
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
        using var trace = telemetry.StartExecution(
            snapshot,
            checkpoint.Request.PermissionScopes ?? [],
            "worker");
        var remainingDeadline = await persistence.RemainingWorkerDeadlineAsync(
            executionId,
            checkpoint.Plan.Budget.DeadlineMilliseconds,
            cancellationToken);
        if (remainingDeadline <= TimeSpan.Zero)
        {
            await persistence.CancelCheckpointAsync(executionId, CancellationToken.None);
            return await persistence.TerminalAsync(
                snapshot,
                ExecutionStatus.TimedOut,
                new Problem(
                    ErrorCodes.ExecutionTimedOut,
                    "Execution timed out",
                    "The execution exceeded its durable queue deadline.",
                    true),
                CancellationToken.None);
        }

        using var deadline = new CancellationTokenSource(remainingDeadline, clock);
        using var execution = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            deadline.Token);
        try
        {
            if (snapshot.CancellationRequestedAt is not null)
            {
                await persistence.CancelCheckpointAsync(executionId, CancellationToken.None);
                return await persistence.TerminalAsync(
                    snapshot,
                    ExecutionStatus.Cancelled,
                    new Problem(
                        ErrorCodes.ExecutionCancelled,
                        "Execution cancelled",
                        "The execution was cancelled before a worker started it."),
                    CancellationToken.None);
            }

            return await plans.RunPlanAsync(
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
            await persistence.CancelCheckpointAsync(executionId, CancellationToken.None);
            return await persistence.TerminalAsync(
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

            await persistence.CancelCheckpointAsync(executionId, CancellationToken.None);
            return await persistence.TerminalAsync(
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
            return await persistence.HandleResumedFailureAsync(snapshot, exception, false);
        }
    }
}
