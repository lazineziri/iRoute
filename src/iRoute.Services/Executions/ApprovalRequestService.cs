using iRoute.Common;

namespace iRoute.Services;

public sealed class ApprovalRequestService(
    IApprovalStore approvals,
    TimeProvider clock)
{
    internal async Task<ApprovalRecord> CreateApprovalAsync(
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
}
