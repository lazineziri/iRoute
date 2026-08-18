using System.Text.Json;
using iRoute.Common;

namespace iRoute.Services;

public sealed partial class BoundedDependencyScheduler
{
    private async Task FailStepAsync(
        Guid executionId,
        string stepId,
        WorkflowStepStatus status,
        Problem problem,
        CancellationToken cancellationToken)
    {
        await checkpoints.FailStepAsync(
            executionId,
            stepId,
            status,
            problem,
            clock.GetUtcNow(),
            cancellationToken);
        await AppendEventAsync(
            executionId,
            ExecutionEventTypes.StepFailed,
            new { stepId, status, problem.Code, problem.Retryable },
            cancellationToken);
    }

    private async Task CancelIncompleteAsync(Guid executionId, CancellationToken cancellationToken)
    {
        var problem = new Problem(
            ErrorCodes.ExecutionCancelled,
            "Workflow stopped",
            "The workflow stopped before all steps completed.");
        await checkpoints.CancelIncompleteStepsAsync(
            executionId,
            problem,
            clock.GetUtcNow(),
            CancellationToken.None);
        cancellationToken.ThrowIfCancellationRequested();
    }

    private async Task AppendEventAsync(
        Guid executionId,
        string eventType,
        object data,
        CancellationToken cancellationToken)
    {
        await _eventWriteLock.WaitAsync(cancellationToken);
        try
        {
            await executions.AppendEventAsync(
                executionId,
                eventType,
                clock.GetUtcNow(),
                JsonSerializer.SerializeToElement(data),
                cancellationToken);
        }
        finally
        {
            _eventWriteLock.Release();
        }
    }

    private static void ValidateOptions(WorkflowSchedulerOptions value)
    {
        if (value.QueueCapacity < 1)
        {
            throw new InvalidOperationException("Workflow queue capacity must be positive.");
        }

        if (value.MaxParallelSteps < 1)
        {
            throw new InvalidOperationException("Workflow maximum parallel steps must be positive.");
        }

        if (value.RetryBaseDelayMilliseconds < 0 ||
            value.RetryMaxDelayMilliseconds < value.RetryBaseDelayMilliseconds ||
            value.RetryJitterRatio is < 0 or > 1)
        {
            throw new InvalidOperationException("Workflow retry settings are invalid.");
        }
    }

    private static void UpdateMaximum(ref int target, int candidate)
    {
        var current = Volatile.Read(ref target);
        while (candidate > current)
        {
            var observed = Interlocked.CompareExchange(ref target, candidate, current);
            if (observed == current)
            {
                return;
            }

            current = observed;
        }
    }

    public void Dispose() => _eventWriteLock.Dispose();

    private sealed record RoundResult(int PeakQueuedSteps, int BackpressureWaitCount);
}
