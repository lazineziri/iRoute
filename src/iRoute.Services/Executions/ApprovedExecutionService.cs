using iRoute.Common;

namespace iRoute.Services;

public sealed class ApprovedExecutionService(
    IExecutionStore store,
    IExecutionCancellationRegistry cancellations,
    TimeProvider clock,
    PlanExecutionService plans,
    ExecutionPersistenceService persistence)
{
    internal async Task<ExecutionSnapshot> ResumeAsync(ExecutionSnapshot snapshot, WorkflowCheckpoint checkpoint, TaskDefinition definition, CancellationToken cancellationToken)
    {
        var executionId = snapshot.ExecutionId;
        var resumedAt = clock.GetUtcNow();
        var claimed = await store.TryTransitionAsync(
            executionId,
            ExecutionStatus.WaitingForApproval,
            ExecutionStatus.Running,
            resumedAt,
            cancellationToken);
        if (claimed is null)
        {
            var current = await store.GetAsync(executionId, cancellationToken)
                ?? throw new ApprovalSubmissionException(
                    ErrorCodes.ApprovalNotFound,
                    "Approval not found",
                    "The approved execution no longer exists.");
            if (current.Status != ExecutionStatus.WaitingForApproval)
            {
                return current;
            }

            throw new ApprovalSubmissionException(
                ErrorCodes.ApprovalAlreadyDecided,
                "Execution is not awaiting approval",
                $"Execution '{executionId}' could not be claimed for approved execution.");
        }

        snapshot = claimed;
        var registeredCancellation = default(CancellationToken);
        CancellationTokenSource? deadlineSource = null;
        CancellationTokenSource? executionSource = null;
        var cancellationRegistered = false;
        try
        {
            await persistence.AppendEventAsync(
                executionId,
                ExecutionEventTypes.StatusChanged,
                new { from = ExecutionStatus.WaitingForApproval, to = ExecutionStatus.Running },
                cancellationToken);
            registeredCancellation = cancellations.Register(executionId, cancellationToken);
            cancellationRegistered = true;
            deadlineSource = new CancellationTokenSource(
                TimeSpan.FromMilliseconds(checkpoint.Plan.Budget.DeadlineMilliseconds), clock);
            executionSource = CancellationTokenSource.CreateLinkedTokenSource(
                registeredCancellation,
                deadlineSource.Token);
            var current = await store.GetAsync(executionId, cancellationToken);
            if (current?.CancellationRequestedAt is not null)
                throw new OperationCanceledException("The approved execution has a durable cancellation request.");
            snapshot = await plans.RunPlanAsync(
                snapshot,
                checkpoint.Request,
                definition,
                checkpoint.Plan,
                checkpoint.Routing,
                false,
                executionSource.Token);
        }
        catch (Exception exception)
        {
            snapshot = await persistence.HandleResumedFailureAsync(
                snapshot,
                exception,
                deadlineSource?.IsCancellationRequested is true &&
                    !registeredCancellation.IsCancellationRequested);
        }
        finally
        {
            executionSource?.Dispose();
            deadlineSource?.Dispose();
            if (cancellationRegistered)
            {
                cancellations.Complete(executionId);
            }
        }

        return snapshot;
    }
}
