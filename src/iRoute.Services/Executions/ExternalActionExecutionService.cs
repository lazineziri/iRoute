using System.Diagnostics;
using iRoute.Common;

namespace iRoute.Services;

public sealed partial class ExecutionService
{
    private async Task<ModelGatewayResult> ExecuteExternalActionAsync(
        ExecutionSnapshot snapshot,
        TaskRequest request,
        ExecutionPlanStep step,
        CancellationToken cancellationToken)
    {
        var inputReference = CanonicalJson.Hash(request.Input);
        var idempotencyReference = PolicyReferences.CreateActionIdempotencyReference(
            snapshot.TenantId,
            request.IdempotencyKey!,
            step.Id,
            step.Capability);
        var now = clock.GetUtcNow();
        var reservation = await externalActions.ReserveAsync(
            new ExternalActionRecord(
                snapshot.ExecutionId,
                snapshot.TenantId,
                step.Id,
                step.Capability,
                idempotencyReference,
                inputReference,
                ExternalActionStatus.Running,
                now,
                now),
            cancellationToken);
        if (reservation.Kind == ExternalActionReservationKind.Reused)
        {
            var reused = reservation.Action.Result
                ?? throw new ExternalActionExecutionException(
                    ErrorCodes.ExternalActionFailed,
                    "External action result missing",
                    "The completed external action has no durable result.");
            await AppendEventAsync(
                snapshot.ExecutionId,
                ExecutionEventTypes.ExternalActionReused,
                new
                {
                    actionId = step.Id,
                    capability = step.Capability,
                    inputReference,
                    idempotencyReference,
                    resultReference = CanonicalJson.Hash(reused.Output)
                },
                cancellationToken);
            return new ModelGatewayResult(
                reused.Output,
                new UsageSummary(ToolCalls: 1),
                1m,
                reused.Evidence);
        }

        if (reservation.Kind != ExternalActionReservationKind.Acquired)
        {
            var (code, title, detail, retryable) = reservation.Kind switch
            {
                ExternalActionReservationKind.Conflict => (
                    ErrorCodes.ExternalActionIdempotencyConflict,
                    "External action idempotency conflict",
                    "The idempotency reference is already bound to a different action or input.",
                    false),
                ExternalActionReservationKind.InProgress => (
                    ErrorCodes.ExternalActionInProgress,
                    "External action already in progress",
                    "A previous attempt reserved this action; reconciliation is required before retrying.",
                    true),
                _ => (
                    ErrorCodes.ExternalActionFailed,
                    "External action previously failed",
                    "The idempotent external action is in a failed state and was not executed again.",
                    false)
            };
            throw new ExternalActionExecutionException(code, title, detail, retryable);
        }

        await AppendEventAsync(
            snapshot.ExecutionId,
            ExecutionEventTypes.ExternalActionStarted,
            new
            {
                actionId = step.Id,
                capability = step.Capability,
                sideEffectClass = step.SideEffectClass,
                actorId = snapshot.ActorId,
                inputReference,
                idempotencyReference
            },
            cancellationToken);
        try
        {
            var stopwatch = Stopwatch.StartNew();
            var result = await externalActionExecutor.ExecuteAsync(
                new ExternalActionRequest(
                    snapshot.ExecutionId,
                    step.Id,
                    step.Capability,
                    request.Input,
                    idempotencyReference,
                    snapshot.TenantId,
                    snapshot.ActorId,
                    request.ProjectId,
                    request.PermissionScopes,
                    TaskPolicyEngine.CurrentPolicyVersion,
                    step.SideEffectClass,
                    step.TimeoutMilliseconds),
                cancellationToken);
            stopwatch.Stop();
            // The side effect has returned success. Persist that fact even if cancellation raced
            // with the response; treating a known success as indeterminate would invite a repeat.
            await externalActions.CompleteAsync(
                snapshot.TenantId,
                idempotencyReference,
                result,
                clock.GetUtcNow(),
                CancellationToken.None);
            await AppendEventAsync(
                snapshot.ExecutionId,
                ExecutionEventTypes.ExternalActionCompleted,
                new
                {
                    actionId = step.Id,
                    capability = step.Capability,
                    inputReference,
                    idempotencyReference,
                    resultReference = CanonicalJson.Hash(result.Output),
                    durationMilliseconds = stopwatch.ElapsedMilliseconds
                },
                CancellationToken.None);
            return new ModelGatewayResult(
                result.Output,
                new UsageSummary(DurationMilliseconds: stopwatch.ElapsedMilliseconds, ToolCalls: 1),
                1m,
                result.Evidence);
        }
        catch (OperationCanceledException)
        {
            await AppendEventAsync(
                snapshot.ExecutionId,
                ExecutionEventTypes.ExternalActionFailed,
                new
                {
                    actionId = step.Id,
                    capability = step.Capability,
                    inputReference,
                    idempotencyReference,
                    status = "indeterminate",
                    retryable = false
                },
                CancellationToken.None);
            throw;
        }
        catch (Exception exception)
        {
            var problem = new Problem(
                ErrorCodes.ExternalActionFailed,
                "External action failed",
                exception.Message);
            await externalActions.FailAsync(
                snapshot.TenantId,
                idempotencyReference,
                problem,
                clock.GetUtcNow(),
                CancellationToken.None);
            await AppendEventAsync(
                snapshot.ExecutionId,
                ExecutionEventTypes.ExternalActionFailed,
                new
                {
                    actionId = step.Id,
                    capability = step.Capability,
                    inputReference,
                    idempotencyReference,
                    status = "failed",
                    retryable = false
                },
                CancellationToken.None);
            throw new ExternalActionExecutionException(
                problem.Code,
                problem.Title,
                problem.Detail,
                innerException: exception);
        }
    }

    private async Task<ApprovalRecord> CreateApprovalAsync(
        ExecutionSnapshot snapshot,
        TaskRequest request,
        ExecutionPlan plan,
        PolicyEvaluation policy,
        CancellationToken cancellationToken)
    {
        var step = plan.Steps.LastOrDefault(item =>
            string.Equals(item.Capability, policy.Capability, StringComparison.Ordinal) &&
            item.SideEffectClass == policy.SideEffectClass)
            ?? throw new InvalidExecutionPlanException(
            [
                new ExecutionPlanValidationIssue(
                    "approval_action_missing",
                    "steps",
                    "The policy-selected approval action does not exist in the execution plan.")
            ]);
        var approval = new ApprovalRecord(
            snapshot.ExecutionId,
            snapshot.TenantId,
            step.Id,
            ApprovalStatus.Pending,
            step.Capability,
            step.SideEffectClass,
            policy.RequiredPermissionScopes,
            snapshot.ActorId,
            null,
            CanonicalJson.Hash(request.Input),
            PolicyReferences.CreateActionIdempotencyReference(
                snapshot.TenantId,
                request.IdempotencyKey!,
                step.Id,
                step.Capability),
            clock.GetUtcNow());
        return await approvals.CreatePendingAsync(approval, cancellationToken);
    }

    private async Task AppendPolicyEventAsync(
        ExecutionSnapshot snapshot,
        PolicyEvaluation policy,
        CancellationToken cancellationToken,
        string? actorId = null)
    {
        var data = new
        {
            policyVersion = policy.PolicyVersion,
            decision = policy.Decision,
            capability = policy.Capability,
            sideEffectClass = policy.SideEffectClass,
            requiredPermissionScopes = policy.RequiredPermissionScopes,
            missingPermissionScopes = policy.MissingPermissionScopes,
            code = policy.Code,
            actorId = actorId ?? snapshot.ActorId,
            tenantId = snapshot.TenantId,
            projectId = snapshot.ProjectId
        };
        await AppendEventAsync(
            snapshot.ExecutionId,
            ExecutionEventTypes.PolicyEvaluated,
            data,
            cancellationToken);
        if (policy.Decision == PolicyDecisionKind.Denied)
        {
            await AppendEventAsync(
                snapshot.ExecutionId,
                ExecutionEventTypes.CapabilityDenied,
                data,
                cancellationToken);
        }
    }

    private async Task<ExecutionSnapshot> HandleResumedFailureAsync(
        ExecutionSnapshot snapshot,
        Exception exception,
        bool timedOut)
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
                new Problem(gateway.Code, "Model gateway failed", gateway.Message, gateway.Retryable)),
            _ => (
                ExecutionStatus.Failed,
                new Problem(ErrorCodes.ExecutionFailed, "Execution failed", exception.Message))
        };
        return await TerminalAsync(snapshot, status, problem, CancellationToken.None);
    }

}
