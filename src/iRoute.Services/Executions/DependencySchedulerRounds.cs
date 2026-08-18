using System.Collections.Concurrent;
using System.Text.Json;
using System.Threading.Channels;
using iRoute.Common;

namespace iRoute.Services;

public sealed partial class BoundedDependencyScheduler
{
    private async Task<RoundResult> ExecuteRoundAsync(
        Guid executionId,
        ExecutionPlan plan,
        ExecutionPlanStep[] ready,
        ConcurrentDictionary<string, WorkflowStepStatus> states,
        ConcurrentDictionary<string, JsonElement> outputs,
        WorkflowStepHandler handler,
        bool preserveCheckpointOnCancellation,
        CancellationToken cancellationToken)
    {
        var queueCapacity = options.QueueCapacity;
        var channel = Channel.CreateBounded<ExecutionPlanStep>(new BoundedChannelOptions(queueCapacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleWriter = true,
            SingleReader = false,
            AllowSynchronousContinuations = false
        });
        using var roundCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var peakQueued = 0;
        var backpressureWaits = 0;
        Exception? failure = null;

        var workers = Enumerable.Range(
                0,
                Math.Min(ready.Length, Math.Min(plan.Budget.MaxParallelCalls, options.MaxParallelSteps)))
            .Select(_ => RunWorkerAsync())
            .ToArray();
        var producer = ProduceAsync();

        try
        {
            await Task.WhenAll(workers.Prepend(producer));
        }
        catch (OperationCanceledException) when (failure is not null)
        {
            // The first worker failure cancels the round so queued siblings never begin.
        }

        if (failure is not null)
        {
            throw failure;
        }

        cancellationToken.ThrowIfCancellationRequested();
        return new RoundResult(peakQueued, backpressureWaits);

        async Task ProduceAsync()
        {
            try
            {
                foreach (var step in ready)
                {
                    if (!channel.Writer.TryWrite(step))
                    {
                        Interlocked.Increment(ref backpressureWaits);
                        await channel.Writer.WriteAsync(step, roundCancellation.Token);
                    }

                    if (channel.Reader.CanCount)
                    {
                        UpdateMaximum(ref peakQueued, channel.Reader.Count);
                    }
                }
            }
            finally
            {
                channel.Writer.TryComplete();
            }
        }

        async Task RunWorkerAsync()
        {
            try
            {
                await foreach (var step in channel.Reader.ReadAllAsync(roundCancellation.Token))
                {
                    roundCancellation.Token.ThrowIfCancellationRequested();
                    try
                    {
                        await ExecuteStepAsync(
                            executionId,
                            step,
                            states,
                            outputs,
                            handler,
                            preserveCheckpointOnCancellation,
                            roundCancellation.Token);
                    }
                    catch (Exception exception)
                    {
                        if (Interlocked.CompareExchange(ref failure, exception, null) is null)
                        {
                            roundCancellation.Cancel();
                        }

                        return;
                    }
                }
            }
            catch (OperationCanceledException) when (roundCancellation.IsCancellationRequested)
            {
            }
        }
    }

    private async Task ExecuteStepAsync(
        Guid executionId,
        ExecutionPlanStep step,
        ConcurrentDictionary<string, WorkflowStepStatus> states,
        ConcurrentDictionary<string, JsonElement> outputs,
        WorkflowStepHandler handler,
        bool preserveCheckpointOnCancellation,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            var startedAt = clock.GetUtcNow();
            var checkpoint = await checkpoints.StartStepAsync(
                executionId,
                step.Id,
                startedAt,
                cancellationToken);
            states[step.Id] = WorkflowStepStatus.Running;
            await AppendEventAsync(
                executionId,
                ExecutionEventTypes.StepStarted,
                new { stepId = step.Id, checkpoint.Attempt, step.Kind, step.Capability },
                cancellationToken);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromMilliseconds(step.TimeoutMilliseconds));
            try
            {
                var dependencyOutputs = step.DependsOn.ToDictionary(
                    dependency => dependency,
                    dependency => outputs[dependency].Clone(),
                    StringComparer.Ordinal);
                var output = await handler(step, dependencyOutputs, timeout.Token);
                var completedAt = clock.GetUtcNow();
                await checkpoints.CompleteStepAsync(
                    executionId,
                    step.Id,
                    output,
                    completedAt,
                    cancellationToken);
                outputs[step.Id] = output.Clone();
                states[step.Id] = WorkflowStepStatus.Succeeded;
                await AppendEventAsync(
                    executionId,
                    ExecutionEventTypes.StepCompleted,
                    new { stepId = step.Id, checkpoint.Attempt },
                    cancellationToken);
                return;
            }
            catch (OperationCanceledException) when (
                timeout.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
            {
                var exception = new WorkflowStepTimedOutException(step.Id, step.TimeoutMilliseconds);
                if (await TryScheduleRetryAsync(
                        executionId,
                        step,
                        checkpoint.Attempt,
                        exception,
                        states,
                        cancellationToken))
                {
                    continue;
                }

                var problem = new Problem(
                    ErrorCodes.WorkflowStepTimedOut,
                    "Workflow step timed out",
                    exception.Message,
                    true);
                await FailStepAsync(
                    executionId,
                    step.Id,
                    WorkflowStepStatus.TimedOut,
                    problem,
                    CancellationToken.None);
                states[step.Id] = WorkflowStepStatus.TimedOut;
                throw exception;
            }
            catch (OperationCanceledException) when (preserveCheckpointOnCancellation)
            {
                throw;
            }
            catch (LeaseFencedException)
            {
                throw;
            }
            catch (OperationCanceledException)
            {
                var problem = new Problem(
                    ErrorCodes.ExecutionCancelled,
                    "Workflow step cancelled",
                    $"Workflow step '{step.Id}' was cancelled.");
                await FailStepAsync(
                    executionId,
                    step.Id,
                    WorkflowStepStatus.Cancelled,
                    problem,
                    CancellationToken.None);
                states[step.Id] = WorkflowStepStatus.Cancelled;
                throw;
            }
            catch (Exception exception)
            {
                if (await TryScheduleRetryAsync(
                        executionId,
                        step,
                        checkpoint.Attempt,
                        exception,
                        states,
                        cancellationToken))
                {
                    continue;
                }

                var wrapped = exception as WorkflowStepExecutionException
                    ?? new WorkflowStepExecutionException(
                        step.Id,
                        $"Workflow step '{step.Id}' failed after {checkpoint.Attempt} attempt(s): {exception.Message}",
                        exception);
                var problem = exception switch
                {
                    ModelGatewayException gatewayException => new Problem(
                        gatewayException.Code,
                        "Model gateway failed",
                        gatewayException.Message,
                        gatewayException.Retryable),
                    ExternalActionExecutionException actionException => new Problem(
                        actionException.Code,
                        actionException.Title,
                        actionException.Message,
                        actionException.Retryable),
                    CapabilityInvocationException capabilityException => new Problem(
                        capabilityException.Code,
                        "Capability invocation failed",
                        capabilityException.Message,
                        capabilityException.Retryable),
                    _ => new Problem(
                        ErrorCodes.WorkflowStepFailed,
                        "Workflow step failed",
                        wrapped.Message)
                };
                await FailStepAsync(
                    executionId,
                    step.Id,
                    WorkflowStepStatus.Failed,
                    problem,
                    CancellationToken.None);
                states[step.Id] = WorkflowStepStatus.Failed;
                if (exception is ModelGatewayException or ExternalActionExecutionException or CapabilityInvocationException)
                {
                    throw;
                }

                throw wrapped;
            }
        }
    }

}
