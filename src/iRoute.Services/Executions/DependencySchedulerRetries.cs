using System.Collections.Concurrent;
using iRoute.Common;

namespace iRoute.Services;

public sealed partial class BoundedDependencyScheduler
{
    private async Task<bool> TryScheduleRetryAsync(
        Guid executionId,
        ExecutionPlanStep step,
        int attempt,
        Exception exception,
        ConcurrentDictionary<string, WorkflowStepStatus> states,
        CancellationToken cancellationToken)
    {
        if (attempt >= step.MaxAttempts || !IsRetryable(exception))
        {
            return false;
        }

        var delay = RetryDelay(executionId, step.Id, attempt, exception);
        await checkpoints.ResetStepForRetryAsync(
            executionId,
            step.Id,
            clock.GetUtcNow(),
            CancellationToken.None);
        states[step.Id] = WorkflowStepStatus.Pending;
        await AppendEventAsync(
            executionId,
            ExecutionEventTypes.StepRetryScheduled,
            new
            {
                stepId = step.Id,
                attempt,
                step.MaxAttempts,
                delayMilliseconds = checked((int)delay.TotalMilliseconds),
                failure = exception.GetType().Name
            },
            CancellationToken.None);
        await Task.Delay(delay, clock, cancellationToken);
        return true;
    }

    private TimeSpan RetryDelay(
        Guid executionId,
        string stepId,
        int attempt,
        Exception exception)
    {
        var exponential = Math.Min(
            options.RetryMaxDelayMilliseconds,
            options.RetryBaseDelayMilliseconds * Math.Pow(2, Math.Max(0, attempt - 1)));
        var jitterPosition = StableJitter(executionId, stepId, attempt);
        var jitterFactor = 1 + ((jitterPosition * 2 - 1) * options.RetryJitterRatio);
        var calculated = TimeSpan.FromMilliseconds(Math.Max(0, exponential * jitterFactor));
        var retryAfter = exception is ModelGatewayException { RetryAfter: { } value }
            ? value
            : TimeSpan.Zero;
        // Retry-After is a server minimum, not a suggestion. Clamp only the locally calculated
        // backoff; shortening a provider value would cause an avoidable retry storm.
        return calculated > retryAfter ? calculated : retryAfter;
    }

    private static double StableJitter(Guid executionId, string stepId, int attempt)
    {
        var hash = 2166136261u;
        foreach (var value in $"{executionId:N}:{stepId}:{attempt}")
        {
            hash ^= value;
            hash *= 16777619u;
        }

        return (hash % 10_001) / 10_000d;
    }

    private static bool IsRetryable(Exception exception) => exception switch
    {
        WorkflowStepTimedOutException => true,
        ModelGatewayException gateway => gateway.Retryable,
        ExternalActionExecutionException action => action.Retryable,
        CapabilityInvocationException capability => capability.Retryable,
        _ => false
    };

}
