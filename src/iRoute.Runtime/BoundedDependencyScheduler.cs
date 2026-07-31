using System.Collections.Concurrent;
using System.Text.Json;
using System.Threading.Channels;
using iRoute.Contracts;
using iRoute.Core;

namespace iRoute.Runtime;

public sealed record WorkflowSchedulerOptions
{
    public int QueueCapacity { get; init; } = 16;
    public int MaxParallelSteps { get; init; } = 4;
}

public delegate Task<JsonElement> WorkflowStepHandler(
    ExecutionPlanStep step,
    IReadOnlyDictionary<string, JsonElement> dependencyOutputs,
    CancellationToken cancellationToken);

public sealed record WorkflowRunResult(
    IReadOnlyDictionary<string, JsonElement> Outputs,
    int PeakQueuedSteps,
    int BackpressureWaitCount,
    int RecoveredStepCount);

public class WorkflowStepExecutionException(
    string stepId,
    string message,
    Exception? innerException = null) : Exception(message, innerException)
{
    public string StepId { get; } = stepId;
}

public sealed class WorkflowStepTimedOutException(string stepId, int timeoutMilliseconds)
    : WorkflowStepExecutionException(
        stepId,
        $"Workflow step '{stepId}' exceeded its {timeoutMilliseconds} millisecond timeout.")
{
}

public sealed class BoundedDependencyScheduler(
    IWorkflowCheckpointStore checkpoints,
    IExecutionStore executions,
    IClock clock,
    WorkflowSchedulerOptions options) : IDisposable
{
    private readonly SemaphoreSlim _eventWriteLock = new(1, 1);

    public async Task<WorkflowRunResult> ResumeAsync(
        Guid executionId,
        WorkflowStepHandler handler,
        CancellationToken cancellationToken)
    {
        var checkpoint = await checkpoints.GetAsync(executionId, cancellationToken)
            ?? throw new KeyNotFoundException($"Workflow checkpoint '{executionId}' was not found.");
        return await ExecuteAsync(
            executionId,
            checkpoint.Request,
            checkpoint.Plan,
            handler,
            cancellationToken);
    }

    public async Task<WorkflowRunResult> ExecuteAsync(
        Guid executionId,
        TaskRequest request,
        ExecutionPlan plan,
        WorkflowStepHandler handler,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(handler);
        ValidateOptions(options);
        var initialization = await checkpoints.InitializeAsync(
            executionId,
            request,
            plan,
            clock.UtcNow,
            cancellationToken);
        if (initialization.Created)
        {
            await AppendEventAsync(
                executionId,
                ExecutionEventTypes.WorkflowCheckpointed,
                new { plan.PlanId, steps = plan.Steps.Count },
                cancellationToken);
        }

        var recovered = await checkpoints.RecoverInterruptedStepsAsync(
            executionId,
            clock.UtcNow,
            cancellationToken);
        if (!initialization.Created)
        {
            await AppendEventAsync(
                executionId,
                ExecutionEventTypes.WorkflowResumed,
                new { recoveredSteps = recovered },
                cancellationToken);
        }

        var checkpoint = await checkpoints.GetAsync(executionId, cancellationToken)
            ?? throw new InvalidOperationException($"Workflow checkpoint '{executionId}' was not found.");
        var states = new ConcurrentDictionary<string, WorkflowStepStatus>(
            checkpoint.Steps.ToDictionary(step => step.StepId, step => step.Status, StringComparer.Ordinal),
            StringComparer.Ordinal);
        var outputs = new ConcurrentDictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var completed in checkpoint.Steps.Where(step =>
                     step.Status == WorkflowStepStatus.Succeeded && step.Output is not null))
        {
            outputs[completed.StepId] = completed.Output!.Value.Clone();
        }

        var peakQueued = 0;
        var backpressureWaits = 0;
        try
        {
            while (states.Values.Any(status => status != WorkflowStepStatus.Succeeded))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var terminal = states.FirstOrDefault(pair => pair.Value is
                    WorkflowStepStatus.Failed or
                    WorkflowStepStatus.Cancelled or
                    WorkflowStepStatus.TimedOut);
                if (!string.IsNullOrEmpty(terminal.Key))
                {
                    throw new WorkflowStepExecutionException(
                        terminal.Key,
                        $"Workflow step '{terminal.Key}' is already {terminal.Value}.");
                }

                var ready = plan.Steps
                    .Where(step =>
                        states[step.Id] == WorkflowStepStatus.Pending &&
                        step.DependsOn.All(dependency => states[dependency] == WorkflowStepStatus.Succeeded))
                    .ToArray();
                if (ready.Length == 0)
                {
                    throw new WorkflowStepExecutionException(
                        "workflow",
                        "No workflow step is ready and the plan is not complete.");
                }

                var round = await ExecuteRoundAsync(
                    executionId,
                    plan,
                    ready,
                    states,
                    outputs,
                    handler,
                    cancellationToken);
                peakQueued = Math.Max(peakQueued, round.PeakQueuedSteps);
                backpressureWaits = checked(backpressureWaits + round.BackpressureWaitCount);
            }
        }
        catch (OperationCanceledException)
        {
            await CancelIncompleteAsync(executionId, cancellationToken);
            throw;
        }
        catch
        {
            await CancelIncompleteAsync(executionId, CancellationToken.None);
            throw;
        }

        return new WorkflowRunResult(
            outputs.ToDictionary(pair => pair.Key, pair => pair.Value.Clone(), StringComparer.Ordinal),
            peakQueued,
            backpressureWaits,
            recovered);
    }

    private async Task<RoundResult> ExecuteRoundAsync(
        Guid executionId,
        ExecutionPlan plan,
        ExecutionPlanStep[] ready,
        ConcurrentDictionary<string, WorkflowStepStatus> states,
        ConcurrentDictionary<string, JsonElement> outputs,
        WorkflowStepHandler handler,
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
        CancellationToken cancellationToken)
    {
        while (true)
        {
            var startedAt = clock.UtcNow;
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
                var completedAt = clock.UtcNow;
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
                if (checkpoint.Attempt < step.MaxAttempts)
                {
                    await checkpoints.ResetStepForRetryAsync(
                        executionId,
                        step.Id,
                        clock.UtcNow,
                        CancellationToken.None);
                    states[step.Id] = WorkflowStepStatus.Pending;
                    await AppendEventAsync(
                        executionId,
                        ExecutionEventTypes.StepRetryScheduled,
                        new { stepId = step.Id, checkpoint.Attempt, step.MaxAttempts },
                        CancellationToken.None);
                    continue;
                }

                var wrapped = exception as WorkflowStepExecutionException
                    ?? new WorkflowStepExecutionException(
                        step.Id,
                        $"Workflow step '{step.Id}' failed after {checkpoint.Attempt} attempt(s): {exception.Message}",
                        exception);
                var problem = exception is ModelGatewayException gatewayException
                    ? new Problem(
                        gatewayException.Code,
                        "Model gateway failed",
                        gatewayException.Message,
                        gatewayException.Retryable)
                    : new Problem(
                        ErrorCodes.WorkflowStepFailed,
                        "Workflow step failed",
                        wrapped.Message);
                await FailStepAsync(
                    executionId,
                    step.Id,
                    WorkflowStepStatus.Failed,
                    problem,
                    CancellationToken.None);
                states[step.Id] = WorkflowStepStatus.Failed;
                if (exception is ModelGatewayException)
                {
                    throw;
                }

                throw wrapped;
            }
        }
    }

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
            clock.UtcNow,
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
            clock.UtcNow,
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
                clock.UtcNow,
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
