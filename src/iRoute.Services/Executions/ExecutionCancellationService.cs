using iRoute.Common;

namespace iRoute.Services;

public sealed class ExecutionCancellationService(
    IExecutionStore store,
    IWorkflowCheckpointStore checkpoints,
    IExecutionWorkStore executionWork,
    IExecutionCancellationRegistry cancellations,
    ExecutionPersistenceService persistence,
    TimeProvider clock)
{
    internal async Task<ExecutionSnapshot?> CancelAsync(
        Guid executionId, string tenantId, CancellationToken cancellationToken)
    {
        var snapshot = await store.GetAsync(executionId, cancellationToken);
        if (snapshot is null || !string.Equals(snapshot.TenantId, tenantId, StringComparison.Ordinal))
        {
            return null;
        }

        if (ExecutionStatusFacts.IsTerminal(snapshot.Status))
        {
            throw AlreadyTerminal(executionId, snapshot.Status);
        }

        var requestedAt = clock.GetUtcNow();
        var applied = await store.TryRequestCancellationAsync(executionId, requestedAt, cancellationToken);
        if (!applied)
        {
            var settled = await store.GetAsync(executionId, cancellationToken);
            if (settled is null || ExecutionStatusFacts.IsTerminal(settled.Status))
                throw AlreadyTerminal(executionId, settled?.Status);
            snapshot = settled;
            requestedAt = settled.CancellationRequestedAt ?? requestedAt;
        }

        if (applied)
            await persistence.AppendEventAsync(executionId, ExecutionEventTypes.CancellationRequested,
                new { requestedAt }, cancellationToken);
        var problem = new Problem(ErrorCodes.ExecutionCancelled, "Execution cancelled",
            snapshot.Status == ExecutionStatus.WaitingForApproval
                ? "The execution was cancelled while waiting for approval."
                : "The execution was cancelled before a worker claimed it.");
        var cancelledBeforeLease = snapshot.Status == ExecutionStatus.Queued &&
            await executionWork.CancelPendingAsync(executionId, requestedAt, problem, cancellationToken);
        var cancelledWhileWaiting = snapshot.Status == ExecutionStatus.WaitingForApproval
            ? await store.TryTransitionAsync(executionId, ExecutionStatus.WaitingForApproval,
                ExecutionStatus.Cancelled, requestedAt, cancellationToken)
            : null;
        if (cancelledWhileWaiting is not null || cancelledBeforeLease)
        {
            await checkpoints.CancelIncompleteStepsAsync(executionId, problem, requestedAt, cancellationToken);
            snapshot = cancelledBeforeLease
                ? await persistence.RecordTerminalEventsAsync(
                    await store.GetAsync(executionId, cancellationToken) ?? snapshot,
                    ExecutionStatus.Queued, problem, cancellationToken)
                : await RecordWaitingCancellationAsync(cancelledWhileWaiting!, problem, cancellationToken);
        }
        else
        {
            cancellations.RequestCancellation(executionId);
            snapshot = await store.GetAsync(executionId, cancellationToken) ?? snapshot;
        }

        return snapshot;
    }

    private async Task<ExecutionSnapshot> RecordWaitingCancellationAsync(
        ExecutionSnapshot snapshot, Problem problem, CancellationToken cancellationToken)
    {
        snapshot = snapshot with { Error = problem };
        await store.UpdateAsync(snapshot, cancellationToken);
        return await persistence.RecordTerminalEventsAsync(snapshot, ExecutionStatus.WaitingForApproval, problem, cancellationToken);
    }

    private static ExecutionCommandException AlreadyTerminal(Guid executionId, ExecutionStatus? status) => new(
        ErrorCodes.ExecutionAlreadyTerminal, "Execution already terminal",
        $"Execution '{executionId}' is already {status?.ToString() ?? "terminal"}.");
}
