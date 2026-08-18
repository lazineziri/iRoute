using iRoute.Common;

namespace iRoute.Services;

public sealed partial class ExecutionService
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

    private async Task<ApprovalResult> SubmitApprovalCoreAsync(
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

        using var trace = _telemetry.StartExecution(snapshot, permissionScopes, "resume");

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

        var approverPolicy = policyEngine.EvaluateApproval(approval, permissionScopes);
        await AppendPolicyEventAsync(snapshot, approverPolicy, cancellationToken, actorId);
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
            await AppendEventAsync(
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
                snapshot = await TerminalAsync(
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
        await AppendPolicyEventAsync(snapshot, executionPolicy, cancellationToken, actorId);
        if (executionPolicy.Decision != PolicyDecisionKind.Allowed)
        {
            snapshot = await TerminalAsync(
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
            snapshot = await QueueAsync(
                snapshot,
                ExecutionStatus.WaitingForApproval,
                cancellationToken);
            return new ApprovalResult(approval.ToSnapshot(), snapshot);
        }

        var resumedAt = clock.GetUtcNow();
        var claimed = await store.TryTransitionAsync(
            executionId,
            ExecutionStatus.WaitingForApproval,
            ExecutionStatus.Running,
            resumedAt,
            cancellationToken);
        if (claimed is null)
        {
            var current = await store.GetAsync(executionId, cancellationToken)
                ?? throw new ApprovalSubmissionException(
                    ErrorCodes.ApprovalNotFound,
                    "Approval not found",
                    "The approved execution no longer exists.");
            if (current.Status != ExecutionStatus.WaitingForApproval)
            {
                return new ApprovalResult(approval.ToSnapshot(), current);
            }

            throw new ApprovalSubmissionException(
                ErrorCodes.ApprovalAlreadyDecided,
                "Execution is not awaiting approval",
                $"Execution '{executionId}' could not be claimed for approved execution.");
        }

        snapshot = claimed;
        var registeredCancellation = default(CancellationToken);
        CancellationTokenSource? deadlineSource = null;
        CancellationTokenSource? executionSource = null;
        var cancellationRegistered = false;
        try
        {
            await AppendEventAsync(
                executionId,
                ExecutionEventTypes.StatusChanged,
                new { from = ExecutionStatus.WaitingForApproval, to = ExecutionStatus.Running },
                cancellationToken);
            registeredCancellation = cancellations.Register(executionId, cancellationToken);
            cancellationRegistered = true;
            deadlineSource = new CancellationTokenSource();
            deadlineSource.CancelAfter(
                TimeSpan.FromMilliseconds(checkpoint.Plan.Budget.DeadlineMilliseconds));
            executionSource = CancellationTokenSource.CreateLinkedTokenSource(
                registeredCancellation,
                deadlineSource.Token);
            snapshot = await RunPlanAsync(
                snapshot,
                checkpoint.Request,
                definition,
                checkpoint.Plan,
                checkpoint.Routing,
                false,
                executionSource.Token);
        }
        catch (Exception exception)
        {
            snapshot = await HandleResumedFailureAsync(
                snapshot,
                exception,
                deadlineSource?.IsCancellationRequested is true &&
                    !registeredCancellation.IsCancellationRequested);
        }
        finally
        {
            executionSource?.Dispose();
            deadlineSource?.Dispose();
            if (cancellationRegistered)
            {
                cancellations.Complete(executionId);
            }
        }

        return new ApprovalResult(approval.ToSnapshot(), snapshot);
    }

}
