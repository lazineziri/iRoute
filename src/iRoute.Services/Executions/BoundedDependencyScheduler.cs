using System.Collections.Concurrent;
using System.Text.Json;
using iRoute.Common;

namespace iRoute.Services;

public sealed partial class BoundedDependencyScheduler(
    IWorkflowCheckpointStore checkpoints,
    IExecutionStore executions,
    TimeProvider clock,
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
            checkpoint.Routing,
            handler,
            cancellationToken);
    }

    public async Task<WorkflowRunResult> ExecuteAsync(
        Guid executionId,
        TaskRequest request,
        ExecutionPlan plan,
        RoutingDecision routing,
        WorkflowStepHandler handler,
        CancellationToken cancellationToken,
        bool preserveCheckpointOnCancellation = false)
    {
        ArgumentNullException.ThrowIfNull(handler);
        ValidateOptions(options);
        var initialization = await checkpoints.InitializeAsync(
            executionId,
            request,
            plan,
            routing,
            clock.GetUtcNow(),
            cancellationToken);
        var hadPriorExecution = initialization.Checkpoint.Steps.Any(step =>
            step.Attempt > 0 || step.Status != WorkflowStepStatus.Pending);
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
            clock.GetUtcNow(),
            cancellationToken);
        if (!initialization.Created && (hadPriorExecution || recovered > 0))
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
                    preserveCheckpointOnCancellation,
                    cancellationToken);
                peakQueued = Math.Max(peakQueued, round.PeakQueuedSteps);
                backpressureWaits = checked(backpressureWaits + round.BackpressureWaitCount);
            }
        }
        catch (OperationCanceledException) when (preserveCheckpointOnCancellation)
        {
            throw;
        }
        catch (LeaseFencedException)
        {
            // The current worker no longer owns the execution. Any cleanup write here would be
            // stale too, so let the new owner recover the checkpoint.
            throw;
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

}
