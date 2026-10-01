using iRoute.Common;

namespace iRoute.Services;

internal static class ExecutionFailure
{
    internal static (ExecutionStatus Status, Problem Problem) Map(Exception exception, bool timedOut)
    {
        var (status, problem) = exception switch
        {
            OperationCanceledException when timedOut => (
                ExecutionStatus.TimedOut,
                new Problem(ErrorCodes.ExecutionTimedOut, "Execution timed out", "The execution exceeded its deadline.", true)),
            OperationCanceledException => (
                ExecutionStatus.Cancelled,
                new Problem(ErrorCodes.ExecutionCancelled, "Execution cancelled", "The execution was cancelled.")),
            TaskExecutionException task => (
                ExecutionStatus.Failed,
                new Problem(task.Code, task.Title, task.Message, task.Retryable)),
            ContextCompilationException context => (
                ExecutionStatus.Failed,
                new Problem(context.Code, context.Title, context.Message)),
            RoutingException routing => (
                ExecutionStatus.Failed,
                new Problem(routing.Code, routing.Title, routing.Message)),
            InvalidExecutionPlanException plan => (
                ExecutionStatus.Failed,
                new Problem(ErrorCodes.InvalidExecutionPlan, "Execution plan is invalid", plan.Message,
                    Metadata: new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["issueCount"] = plan.Issues.Count.ToString(System.Globalization.CultureInfo.InvariantCulture)
                    })),
            ExternalActionExecutionException action => (
                ExecutionStatus.Failed,
                new Problem(action.Code, action.Title, action.Message, action.Retryable)),
            CapabilityInvocationException capability => (
                ExecutionStatus.Failed,
                CapabilityProblem(capability)),
            WorkflowStepTimedOutException step => (
                ExecutionStatus.TimedOut,
                new Problem(
                    ErrorCodes.WorkflowStepTimedOut,
                    "Workflow step timed out",
                    step.Message,
                    true,
                    new Dictionary<string, string> { ["stepId"] = step.StepId })),
            WorkflowStepExecutionException step => (
                ExecutionStatus.Failed,
                new Problem(
                    ErrorCodes.WorkflowStepFailed,
                    "Workflow step failed",
                    step.Message,
                    Metadata: new Dictionary<string, string> { ["stepId"] = step.StepId })),
            ModelGatewayException gateway => (
                ExecutionStatus.Failed,
                GatewayProblem(gateway)),
            _ => (
                ExecutionStatus.Failed,
                new Problem(ErrorCodes.ExecutionFailed, "Execution failed", exception.Message))
        };
        return (status, problem);
    }

    private static Problem GatewayProblem(ModelGatewayException exception)
    {
        var metadata = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["gatewayFailureKind"] = exception.FailureKind.ToString()
        };
        if (exception.StatusCode is { } statusCode)
        {
            metadata["gatewayStatusCode"] = statusCode.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        if (!string.IsNullOrWhiteSpace(exception.GatewayId))
        {
            metadata["gatewayId"] = exception.GatewayId;
        }

        return new Problem(exception.Code, "Model gateway failed", exception.Message, exception.Retryable, metadata);
    }
    private static Problem CapabilityProblem(CapabilityInvocationException exception)
    {
        var metadata = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["capabilityFailureKind"] = exception.FailureKind.ToString()
        };
        if (!string.IsNullOrWhiteSpace(exception.Capability))
        {
            metadata["capability"] = exception.Capability;
        }

        if (!string.IsNullOrWhiteSpace(exception.ConnectorId))
        {
            metadata["connectorId"] = exception.ConnectorId;
        }

        return new Problem(
            exception.Code,
            "Capability invocation failed",
            exception.Message,
            exception.Retryable,
            metadata);
    }


}
