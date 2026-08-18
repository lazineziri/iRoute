using System.Text.Json;

namespace iRoute.Common;

public enum WorkflowStepStatus
{
    Pending,
    Running,
    Succeeded,
    Failed,
    Cancelled,
    TimedOut
}

public sealed record WorkflowStepCheckpoint(
    Guid ExecutionId,
    string StepId,
    WorkflowStepStatus Status,
    int Attempt,
    DateTimeOffset? StartedAt = null,
    DateTimeOffset? CompletedAt = null,
    JsonElement? Output = null,
    Problem? Error = null);

public sealed record WorkflowCheckpoint(
    Guid ExecutionId,
    TaskRequest Request,
    ExecutionPlan Plan,
    RoutingDecision Routing,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    IReadOnlyList<WorkflowStepCheckpoint> Steps);

public sealed record WorkflowCheckpointInitialization(
    WorkflowCheckpoint Checkpoint,
    bool Created);

public interface IWorkflowCheckpointStore
{
    Task<WorkflowCheckpointInitialization> InitializeAsync(
        Guid executionId,
        TaskRequest request,
        ExecutionPlan plan,
        RoutingDecision routing,
        DateTimeOffset createdAt,
        CancellationToken cancellationToken);

    Task<WorkflowCheckpoint?> GetAsync(
        Guid executionId,
        CancellationToken cancellationToken);

    Task<int> RecoverInterruptedStepsAsync(
        Guid executionId,
        DateTimeOffset recoveredAt,
        CancellationToken cancellationToken);

    Task<WorkflowStepCheckpoint> StartStepAsync(
        Guid executionId,
        string stepId,
        DateTimeOffset startedAt,
        CancellationToken cancellationToken);

    Task CompleteStepAsync(
        Guid executionId,
        string stepId,
        JsonElement output,
        DateTimeOffset completedAt,
        CancellationToken cancellationToken);

    Task ResetStepForRetryAsync(
        Guid executionId,
        string stepId,
        DateTimeOffset updatedAt,
        CancellationToken cancellationToken);

    Task FailStepAsync(
        Guid executionId,
        string stepId,
        WorkflowStepStatus status,
        Problem problem,
        DateTimeOffset completedAt,
        CancellationToken cancellationToken);

    Task CancelIncompleteStepsAsync(
        Guid executionId,
        Problem problem,
        DateTimeOffset completedAt,
        CancellationToken cancellationToken);
}
