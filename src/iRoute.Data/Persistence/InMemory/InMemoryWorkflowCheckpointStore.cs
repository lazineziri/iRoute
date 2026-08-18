using System.Collections.Concurrent;
using System.Text.Json;
using iRoute.Common;

namespace iRoute.Data;

public sealed class InMemoryWorkflowCheckpointStore(
    IExecutionFence? executionFence = null,
    InMemoryExecutionWorkStore? executionWork = null) : IWorkflowCheckpointStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly ConcurrentDictionary<Guid, WorkflowState> _workflows = new();
    private readonly IExecutionFence _executionFence = executionFence ?? new NullExecutionFence();

    public Task<WorkflowCheckpointInitialization> InitializeAsync(
        Guid executionId,
        TaskRequest request,
        ExecutionPlan plan,
        RoutingDecision routing,
        DateTimeOffset createdAt,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return WithLease(executionId, () =>
        {
            var candidate = new WorkflowState(request, plan, routing, createdAt);
            var state = _workflows.GetOrAdd(executionId, candidate);
            lock (state.Sync)
            {
                EnsureSamePlan(state.Plan, plan);
                EnsureSameRouting(state.Routing, routing);
                return Task.FromResult(new WorkflowCheckpointInitialization(
                    ToCheckpoint(executionId, state),
                    ReferenceEquals(state, candidate)));
            }
        });
    }

    public Task<WorkflowCheckpoint?> GetAsync(Guid executionId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!_workflows.TryGetValue(executionId, out var state))
        {
            return Task.FromResult<WorkflowCheckpoint?>(null);
        }

        lock (state.Sync)
        {
            return Task.FromResult<WorkflowCheckpoint?>(ToCheckpoint(executionId, state));
        }
    }

    public Task<int> RecoverInterruptedStepsAsync(
        Guid executionId,
        DateTimeOffset recoveredAt,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return WithLease(executionId, () =>
        {
            var state = GetState(executionId);
            lock (state.Sync)
            {
                var recovered = 0;
                foreach (var step in state.Steps.Values.Where(step => step.Status == WorkflowStepStatus.Running))
                {
                    step.Status = WorkflowStepStatus.Pending;
                    step.StartedAt = null;
                    step.CompletedAt = null;
                    step.Error = null;
                    recovered++;
                }

                if (recovered > 0)
                {
                    state.UpdatedAt = recoveredAt;
                }

                return Task.FromResult(recovered);
            }
        });
    }

    public Task<WorkflowStepCheckpoint> StartStepAsync(
        Guid executionId,
        string stepId,
        DateTimeOffset startedAt,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return WithLease(executionId, () =>
        {
            var state = GetState(executionId);
            lock (state.Sync)
            {
                var step = GetStep(state, stepId);
                if (step.Status != WorkflowStepStatus.Pending)
                {
                    throw new InvalidOperationException($"Step '{stepId}' cannot start from {step.Status}.");
                }

                step.Status = WorkflowStepStatus.Running;
                step.Attempt = checked(step.Attempt + 1);
                step.StartedAt = startedAt;
                step.CompletedAt = null;
                step.Error = null;
                state.UpdatedAt = startedAt;
                return Task.FromResult(ToCheckpoint(executionId, step));
            }
        });
    }

    public Task CompleteStepAsync(
        Guid executionId,
        string stepId,
        JsonElement output,
        DateTimeOffset completedAt,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return WithLease(executionId, () =>
        {
            var state = GetState(executionId);
            lock (state.Sync)
            {
                var step = GetRunningStep(state, stepId);
                step.Status = WorkflowStepStatus.Succeeded;
                step.Output = output.Clone();
                step.CompletedAt = completedAt;
                step.Error = null;
                state.UpdatedAt = completedAt;
                return Task.CompletedTask;
            }
        });
    }

    public Task ResetStepForRetryAsync(
        Guid executionId,
        string stepId,
        DateTimeOffset updatedAt,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return WithLease(executionId, () =>
        {
            var state = GetState(executionId);
            lock (state.Sync)
            {
                var step = GetRunningStep(state, stepId);
                step.Status = WorkflowStepStatus.Pending;
                step.StartedAt = null;
                step.CompletedAt = null;
                step.Error = null;
                state.UpdatedAt = updatedAt;
                return Task.CompletedTask;
            }
        });
    }

    public Task FailStepAsync(
        Guid executionId,
        string stepId,
        WorkflowStepStatus status,
        Problem problem,
        DateTimeOffset completedAt,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (status is not (WorkflowStepStatus.Failed or WorkflowStepStatus.Cancelled or WorkflowStepStatus.TimedOut))
        {
            throw new ArgumentOutOfRangeException(nameof(status), status, "A failed step requires a terminal failure status.");
        }

        return WithLease(executionId, () =>
        {
            var state = GetState(executionId);
            lock (state.Sync)
            {
                var step = GetStep(state, stepId);
                if (step.Status != WorkflowStepStatus.Running && status != WorkflowStepStatus.Cancelled)
                {
                    throw new InvalidOperationException($"Step '{stepId}' cannot fail from {step.Status}.");
                }

                if (step.Status == WorkflowStepStatus.Succeeded)
                {
                    return Task.CompletedTask;
                }

                step.Status = status;
                step.CompletedAt = completedAt;
                step.Error = problem;
                state.UpdatedAt = completedAt;
                return Task.CompletedTask;
            }
        });
    }

    public Task CancelIncompleteStepsAsync(
        Guid executionId,
        Problem problem,
        DateTimeOffset completedAt,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return WithLease(executionId, () =>
        {
            var state = GetState(executionId);
            lock (state.Sync)
            {
                foreach (var step in state.Steps.Values.Where(step =>
                             step.Status is WorkflowStepStatus.Pending or WorkflowStepStatus.Running))
                {
                    step.Status = WorkflowStepStatus.Cancelled;
                    step.CompletedAt = completedAt;
                    step.Error = problem;
                }

                state.UpdatedAt = completedAt;
                return Task.CompletedTask;
            }
        });
    }

    private static WorkflowCheckpoint ToCheckpoint(Guid executionId, WorkflowState state) => new(
        executionId,
        state.Request,
        state.Plan,
        state.Routing,
        state.CreatedAt,
        state.UpdatedAt,
        state.Steps.Values
            .OrderBy(step => step.StepId, StringComparer.Ordinal)
            .Select(step => ToCheckpoint(executionId, step))
            .ToArray());

    private static WorkflowStepCheckpoint ToCheckpoint(Guid executionId, StepState step) => new(
        executionId,
        step.StepId,
        step.Status,
        step.Attempt,
        step.StartedAt,
        step.CompletedAt,
        step.Output?.Clone(),
        step.Error);

    private T WithLease<T>(Guid executionId, Func<T> action)
    {
        if (_executionFence.CurrentToken is not { } token)
        {
            return action();
        }

        if (executionWork is null)
        {
            throw new InvalidOperationException(
                "An in-memory execution work store is required when a lease fence is active.");
        }

        return executionWork.ExecuteIfOwned(executionId, token, action);
    }

    private WorkflowState GetState(Guid executionId) =>
        _workflows.GetValueOrDefault(executionId)
        ?? throw new KeyNotFoundException($"Workflow checkpoint '{executionId}' does not exist.");

    private static StepState GetStep(WorkflowState state, string stepId) =>
        state.Steps.GetValueOrDefault(stepId)
        ?? throw new KeyNotFoundException($"Workflow step '{stepId}' does not exist.");

    private static StepState GetRunningStep(WorkflowState state, string stepId)
    {
        var step = GetStep(state, stepId);
        if (step.Status != WorkflowStepStatus.Running)
        {
            throw new InvalidOperationException($"Step '{stepId}' is not running.");
        }

        return step;
    }

    private static void EnsureSamePlan(ExecutionPlan current, ExecutionPlan requested)
    {
        if (!string.Equals(
                JsonSerializer.Serialize(current, JsonOptions),
                JsonSerializer.Serialize(requested, JsonOptions),
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException("A different execution plan is already checkpointed.");
        }
    }

    private static void EnsureSameRouting(RoutingDecision current, RoutingDecision requested)
    {
        if (!string.Equals(
                JsonSerializer.Serialize(current, JsonOptions),
                JsonSerializer.Serialize(requested, JsonOptions),
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException("A different routing decision is already checkpointed.");
        }
    }

    private sealed class WorkflowState
    {
        public WorkflowState(
            TaskRequest request,
            ExecutionPlan plan,
            RoutingDecision routing,
            DateTimeOffset createdAt)
        {
            Request = request with { Input = request.Input.Clone() };
            Plan = plan;
            Routing = routing;
            CreatedAt = createdAt;
            UpdatedAt = createdAt;
            Steps = plan.Steps.ToDictionary(
                step => step.Id,
                step => new StepState(step.Id),
                StringComparer.Ordinal);
        }

        public object Sync { get; } = new();
        public TaskRequest Request { get; }
        public ExecutionPlan Plan { get; }
        public RoutingDecision Routing { get; }
        public DateTimeOffset CreatedAt { get; }
        public DateTimeOffset UpdatedAt { get; set; }
        public Dictionary<string, StepState> Steps { get; }
    }

    private sealed class StepState(string stepId)
    {
        public string StepId { get; } = stepId;
        public WorkflowStepStatus Status { get; set; }
        public int Attempt { get; set; }
        public DateTimeOffset? StartedAt { get; set; }
        public DateTimeOffset? CompletedAt { get; set; }
        public JsonElement? Output { get; set; }
        public Problem? Error { get; set; }
    }
}
