using System.Text.Json;
using iRoute.Common;

namespace iRoute.Services;

public sealed class ExternalActionReconciliationService(
    IExecutionStore store,
    IExternalActionStore externalActions,
    ExecutionPersistenceService persistence,
    TimeProvider clock)
{
    internal async Task<IReadOnlyList<UnresolvedExternalAction>?> ListAsync(
        Guid executionId, string tenantId, IReadOnlyCollection<string> permissionScopes,
        CancellationToken cancellationToken)
    {
        if (!await IsVisibleAsync(executionId, tenantId, cancellationToken))
        {
            return null;
        }

        EnsurePermission(permissionScopes);
        var unresolved = await externalActions.ListUnresolvedAsync(tenantId, executionId, cancellationToken);
        return unresolved.Select(ToContract).ToArray();
    }

    internal async Task<UnresolvedExternalAction?> ReconcileAsync(
        Guid executionId, string actionId, ExternalActionReconciliation reconciliation,
        string tenantId, string actorId, IReadOnlyCollection<string> permissionScopes,
        CancellationToken cancellationToken)
    {
        if (!await IsVisibleAsync(executionId, tenantId, cancellationToken))
        {
            return null;
        }

        EnsurePermission(permissionScopes);
        var succeeded = string.Equals(reconciliation.Outcome, "succeeded", StringComparison.OrdinalIgnoreCase);
        if (!succeeded && !string.Equals(reconciliation.Outcome, "failed", StringComparison.OrdinalIgnoreCase))
        {
            throw new ExecutionCommandException(ErrorCodes.InvalidTaskRequest, "Invalid reconciliation outcome",
                "Outcome must be 'succeeded' when the external action completed, or 'failed' when it did not.");
        }

        var unresolved = await externalActions.ListUnresolvedAsync(tenantId, executionId, cancellationToken);
        var target = unresolved.FirstOrDefault(action => string.Equals(action.ActionId, actionId, StringComparison.Ordinal));
        if (target is null)
        {
            return null;
        }

        var now = clock.GetUtcNow();
        var detail = string.IsNullOrWhiteSpace(reconciliation.Detail)
            ? $"Reconciled by '{actorId}'."
            : reconciliation.Detail;
        var record = succeeded
            ? await externalActions.CompleteAsync(tenantId, target.IdempotencyReference,
                new ExternalActionResult(JsonSerializer.SerializeToElement(new
                {
                    reconciled = true,
                    reconciledBy = actorId,
                    detail
                }), [new EvidenceReference("reconciliation", $"actor:{actorId}", ObservedAt: now)]),
                now, cancellationToken)
            : await externalActions.FailAsync(tenantId, target.IdempotencyReference,
                new Problem(ErrorCodes.ExternalActionFailed, "External action reconciled as failed", detail, Retryable: true),
                now, cancellationToken);
        await persistence.AppendEventAsync(executionId, ExecutionEventTypes.ExternalActionReconciled,
            new { actionId = target.ActionId, outcome = succeeded ? "succeeded" : "failed", reconciledBy = actorId },
            cancellationToken);
        return ToContract(record);
    }

    private async Task<bool> IsVisibleAsync(Guid executionId, string tenantId, CancellationToken cancellationToken)
    {
        var snapshot = await store.GetAsync(executionId, cancellationToken);
        return snapshot is not null && string.Equals(snapshot.TenantId, tenantId, StringComparison.Ordinal);
    }

    private static void EnsurePermission(IReadOnlyCollection<string> permissionScopes)
    {
        if (!permissionScopes.Contains(TaskPolicyEngine.ApprovalPermissionScope, StringComparer.Ordinal))
        {
            throw new ExecutionCommandException(ErrorCodes.PermissionScopeDenied, "Permission scope denied",
                $"Reconciling an external action requires the '{TaskPolicyEngine.ApprovalPermissionScope}' scope.");
        }
    }

    private static UnresolvedExternalAction ToContract(ExternalActionRecord record) => new(
        record.ActionId, record.Capability, record.Status.ToString(), record.CreatedAt, record.UpdatedAt);
}
