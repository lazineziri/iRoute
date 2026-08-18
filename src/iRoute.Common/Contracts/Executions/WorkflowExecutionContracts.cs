using System.Text.Json;

namespace iRoute.Common;

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
