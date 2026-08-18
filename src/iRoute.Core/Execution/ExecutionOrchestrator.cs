using iRoute.Common;

namespace iRoute.Core;

public sealed class ExecutionOrchestrator(IExecutionService executions)
{
    public Task<ExecutionSnapshot> ExecuteAsync(
        TaskRequest request,
        CancellationToken cancellationToken) =>
        executions.ExecuteAsync(request, cancellationToken);

    public Task<ExecutionSnapshot> SubmitAsync(
        TaskRequest request,
        CancellationToken cancellationToken) =>
        executions.SubmitAsync(request, cancellationToken);

    public Task<ApprovalResult> SubmitApprovalAsync(
        Guid executionId,
        ApprovalDecision decision,
        string tenantId,
        string actorId,
        IReadOnlyCollection<string> permissionScopes,
        CancellationToken cancellationToken) =>
        executions.SubmitApprovalAsync(
            executionId,
            decision,
            tenantId,
            actorId,
            permissionScopes,
            cancellationToken);

    public Task<ApprovalResult> SubmitApprovalForQueueAsync(
        Guid executionId,
        ApprovalDecision decision,
        string tenantId,
        string actorId,
        IReadOnlyCollection<string> permissionScopes,
        CancellationToken cancellationToken) =>
        executions.SubmitApprovalForQueueAsync(
            executionId,
            decision,
            tenantId,
            actorId,
            permissionScopes,
            cancellationToken);

    public Task<ExecutionSnapshot> ProcessQueuedAsync(
        Guid executionId,
        CancellationToken cancellationToken) =>
        executions.ProcessQueuedAsync(executionId, cancellationToken);
}
