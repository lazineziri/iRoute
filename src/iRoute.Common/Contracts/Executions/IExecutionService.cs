
namespace iRoute.Common;

public interface IExecutionService
{
    Task<ExecutionSnapshot> ExecuteAsync(TaskRequest request, CancellationToken cancellationToken);

    Task<ExecutionSnapshot> SubmitAsync(TaskRequest request, CancellationToken cancellationToken);

    Task<ApprovalResult> SubmitApprovalAsync(
        Guid executionId,
        ApprovalDecision decision,
        string tenantId,
        string actorId,
        IReadOnlyCollection<string> permissionScopes,
        CancellationToken cancellationToken);

    Task<ApprovalResult> SubmitApprovalForQueueAsync(
        Guid executionId,
        ApprovalDecision decision,
        string tenantId,
        string actorId,
        IReadOnlyCollection<string> permissionScopes,
        CancellationToken cancellationToken);

    Task<ExecutionSnapshot> ProcessQueuedAsync(Guid executionId, CancellationToken cancellationToken);
}
