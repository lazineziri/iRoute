using iRoute.Common;

namespace iRoute.Services;

public sealed class ExecutionService(
    ExecutionSubmissionService submissions,
    ExecutionApprovalService approvals,
    QueuedExecutionService queue,
    ExecutionCancellationService cancellations,
    ExternalActionReconciliationService reconciliationService) : IExecutionService
{
    public Task<ExecutionSnapshot> ExecuteAsync(TaskRequest request, CancellationToken cancellationToken) =>
        submissions.ExecuteAsync(request, cancellationToken);

    public Task<ExecutionSnapshot> SubmitAsync(TaskRequest request, CancellationToken cancellationToken) =>
        submissions.SubmitAsync(request, cancellationToken);

    public Task<ApprovalResult> SubmitApprovalAsync(
        Guid executionId, ApprovalDecision decision, string tenantId, string actorId,
        IReadOnlyCollection<string> permissionScopes, CancellationToken cancellationToken) =>
        approvals.SubmitApprovalAsync(executionId, decision, tenantId, actorId, permissionScopes, cancellationToken);

    public Task<ApprovalResult> SubmitApprovalForQueueAsync(
        Guid executionId, ApprovalDecision decision, string tenantId, string actorId,
        IReadOnlyCollection<string> permissionScopes, CancellationToken cancellationToken) =>
        approvals.SubmitApprovalForQueueAsync(executionId, decision, tenantId, actorId, permissionScopes, cancellationToken);

    public Task<ExecutionSnapshot> ProcessQueuedAsync(Guid executionId, CancellationToken cancellationToken) =>
        queue.ProcessQueuedAsync(executionId, cancellationToken);

    public Task<ExecutionSnapshot?> CancelAsync(Guid executionId, string tenantId, CancellationToken cancellationToken) =>
        cancellations.CancelAsync(executionId, tenantId, cancellationToken);

    public Task<IReadOnlyList<UnresolvedExternalAction>?> ListUnresolvedActionsAsync(
        Guid executionId, string tenantId, IReadOnlyCollection<string> permissionScopes,
        CancellationToken cancellationToken) =>
        reconciliationService.ListAsync(executionId, tenantId, permissionScopes, cancellationToken);

    public Task<UnresolvedExternalAction?> ReconcileActionAsync(
        Guid executionId, string actionId, ExternalActionReconciliation reconciliation,
        string tenantId, string actorId, IReadOnlyCollection<string> permissionScopes,
        CancellationToken cancellationToken) =>
        reconciliationService.ReconcileAsync(executionId, actionId, reconciliation, tenantId, actorId, permissionScopes, cancellationToken);
}
