using iRoute.Common;
using static iRoute.Services.ExecutionValidation;

namespace iRoute.Services;

public sealed class ExecutionApprovalService(
    IExecutionStore store,
    IApprovalStore approvals,
    IWorkflowCheckpointStore checkpoints,
    ITaskDefinitionRegistry taskDefinitions,
    ITaskPolicyEngine policyEngine,
    TimeProvider clock,
    IExecutionTelemetry telemetry,
    ApprovedExecutionService runner,
    ExecutionPersistenceService persistence)
{
    public Task<ApprovalResult> SubmitApprovalAsync(
        Guid executionId,
        ApprovalDecision decision,
        string tenantId,
        string actorId,
        IReadOnlyCollection<string> permissionScopes,
        CancellationToken cancellationToken) =>
        SubmitApprovalCoreAsync(
            executionId,
            decision,
            tenantId,
            actorId,
            permissionScopes,
            false,
            cancellationToken);

    public Task<ApprovalResult> SubmitApprovalForQueueAsync(
        Guid executionId,
        ApprovalDecision decision,
        string tenantId,
        string actorId,
        IReadOnlyCollection<string> permissionScopes,
        CancellationToken cancellationToken) =>
        SubmitApprovalCoreAsync(
            executionId,
            decision,
            tenantId,
            actorId,
            permissionScopes,
            true,
            cancellationToken);

    internal async Task<ApprovalResult> SubmitApprovalCoreAsync(
        Guid executionId,
        ApprovalDecision decision,
        string tenantId,
        string actorId,
        IReadOnlyCollection<string> permissionScopes,
        bool deferExecution,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(decision.ActionId))
        {
            throw new ApprovalSubmissionException(
                ErrorCodes.InvalidTaskRequest,
                "Invalid approval decision",
                "ActionId is required.");
        }

        var snapshot = await store.GetAsync(executionId, cancellationToken);
        if (snapshot is null || !string.Equals(snapshot.TenantId, tenantId, StringComparison.Ordinal))
        {
            throw new ApprovalSubmissionException(
                ErrorCodes.ApprovalNotFound,
                "Approval not found",
                "The requested approval was not found.");
        }

        using var trace = telemetry.StartExecution(snapshot, permissionScopes, "resume");

        var approval = await approvals.GetAsync(executionId, decision.ActionId, cancellationToken);
        if (approval is null || !string.Equals(approval.TenantId, tenantId, StringComparison.Ordinal))
        {
            throw new ApprovalSubmissionException(
                ErrorCodes.ApprovalNotFound,
                "Approval not found",
                "The requested approval was not found.");
        }

        if (IsTerminal(snapshot.Status) && approval.Status == ApprovalStatus.Pending)
        {
            throw new ApprovalSubmissionException(
                ErrorCodes.ApprovalAlreadyDecided,
                "Execution is no longer awaiting approval",
                $"Execution '{executionId}' is already {snapshot.Status}.");
        }

        var approverPolicy = policyEngine.EvaluateApproval(approval, actorId, permissionScopes);
        await persistence.AppendPolicyEventAsync(snapshot, approverPolicy, cancellationToken, actorId);
        if (approverPolicy.Decision == PolicyDecisionKind.Denied)
        {
            throw new ApprovalSubmissionException(
                approverPolicy.Code ?? ErrorCodes.PermissionScopeDenied,
                "Approval permission denied",
                approverPolicy.Reason ?? "The actor is not authorized to decide this approval.");
        }

        ApprovalDecisionResult decisionResult;
        try
        {
            decisionResult = await approvals.DecideAsync(
                executionId,
                decision.ActionId,
                decision.Approved,
                actorId,
                decision.Reason,
                clock.GetUtcNow(),
                cancellationToken);
        }
        catch (InvalidOperationException exception)
        {
            throw new ApprovalSubmissionException(
                ErrorCodes.ApprovalAlreadyDecided,
                "Approval already decided",
                exception.Message);
        }

        approval = decisionResult.Approval;
        if (decisionResult.Applied)
        {
            await persistence.AppendEventAsync(
                executionId,
                ExecutionEventTypes.ApprovalDecided,
                new
                {
                    actionId = approval.ActionId,
                    status = approval.Status,
                    decidedByActorId = approval.DecidedByActorId,
                    decidedAt = approval.DecidedAt,
                    reasonProvided = approval.Reason is not null,
                    policyVersion = TaskPolicyEngine.CurrentPolicyVersion
                },
                cancellationToken);
        }

        snapshot = await store.GetAsync(executionId, cancellationToken) ?? snapshot;
        if (!decision.Approved)
        {
            if (!IsTerminal(snapshot.Status))
            {
                snapshot = await persistence.TerminalAsync(
                    snapshot,
                    ExecutionStatus.Failed,
                    new Problem(
                        ErrorCodes.ApprovalDenied,
                        "External action denied",
                        "The proposed external action was denied by an authorized actor."),
                    CancellationToken.None);
            }

            return new ApprovalResult(approval.ToSnapshot(), snapshot);
        }

        if (IsTerminal(snapshot.Status))
        {
            return new ApprovalResult(approval.ToSnapshot(), snapshot);
        }

        if (snapshot.Status != ExecutionStatus.WaitingForApproval)
        {
            throw new ApprovalSubmissionException(
                ErrorCodes.ApprovalAlreadyDecided,
                "Execution is not awaiting approval",
                $"Execution '{executionId}' is currently {snapshot.Status}.");
        }

        var checkpoint = await checkpoints.GetAsync(executionId, cancellationToken)
            ?? throw new ApprovalSubmissionException(
                ErrorCodes.ExecutionFailed,
                "Workflow checkpoint missing",
                "The approved execution has no durable workflow checkpoint.");
        var definition = await taskDefinitions.FindAsync(checkpoint.Request.TaskType, cancellationToken)
            ?? throw new ApprovalSubmissionException(
                ErrorCodes.UnknownTaskType,
                "Unknown task type",
                $"No active task definition exists for '{checkpoint.Request.TaskType}'.");
        var executionPolicy = policyEngine.Evaluate(
            checkpoint.Request,
            definition,
            checkpoint.Plan,
            approval);
        await persistence.AppendPolicyEventAsync(snapshot, executionPolicy, cancellationToken, actorId);
        if (executionPolicy.Decision != PolicyDecisionKind.Allowed)
        {
            snapshot = await persistence.TerminalAsync(
                snapshot,
                ExecutionStatus.Failed,
                new Problem(
                    executionPolicy.Code ?? ErrorCodes.ExecutionFailed,
                    "Approved action failed policy revalidation",
                    executionPolicy.Reason ?? "The approved action no longer passes policy."),
                CancellationToken.None);
            return new ApprovalResult(approval.ToSnapshot(), snapshot);
        }

        if (deferExecution)
        {
            snapshot = await persistence.QueueAsync(
                snapshot,
                ExecutionStatus.WaitingForApproval,
                cancellationToken);
            return new ApprovalResult(approval.ToSnapshot(), snapshot);
        }

        snapshot = await runner.ResumeAsync(snapshot, checkpoint, definition, cancellationToken);
        return new ApprovalResult(approval.ToSnapshot(), snapshot);
    }
}
