using System.Text.Json;
using iRoute.Common;
using iRoute.Services;
using Microsoft.Extensions.Options;

namespace iRoute.Runtime.Api;

public static partial class ExecutionEndpoints
{
    private static async Task<IResult> ListUnresolvedActionsAsync(
        Guid executionId,
        HttpRequest request,
        IOptions<IRouteIdentityOptions> identityOptions,
        IExecutionStore store,
        IExternalActionStore externalActions,
        CancellationToken cancellationToken)
    {
        var identity = RequestIdentity.Resolve(request, identityOptions.Value);
        var snapshot = await store.GetAsync(executionId, cancellationToken);
        if (snapshot is null || !IsVisibleToTenant(snapshot.TenantId, request, identityOptions.Value))
        {
            return Results.NotFound();
        }

        if (!identity.PermissionScopes.Contains(TaskPolicyEngine.ApprovalPermissionScope, StringComparer.Ordinal))
        {
            return Problem(
                StatusCodes.Status403Forbidden,
                ErrorCodes.PermissionScopeDenied,
                "Permission scope denied",
                $"Reconciling an external action requires the '{TaskPolicyEngine.ApprovalPermissionScope}' scope.");
        }

        var unresolved = await externalActions.ListUnresolvedAsync(
            identity.TenantId,
            executionId,
            cancellationToken);
        return Results.Ok(unresolved
            .Select(action => new UnresolvedExternalAction(
                action.ActionId,
                action.Capability,
                action.Status.ToString(),
                action.CreatedAt,
                action.UpdatedAt))
            .ToArray());
    }

    private static async Task<IResult> ReconcileActionAsync(
        Guid executionId,
        string actionId,
        ExternalActionReconciliation reconciliation,
        HttpRequest request,
        IOptions<IRouteIdentityOptions> identityOptions,
        IExecutionStore store,
        IExternalActionStore externalActions,
        TimeProvider clock,
        CancellationToken cancellationToken)
    {
        var identity = RequestIdentity.Resolve(request, identityOptions.Value);
        var snapshot = await store.GetAsync(executionId, cancellationToken);
        if (snapshot is null || !IsVisibleToTenant(snapshot.TenantId, request, identityOptions.Value))
        {
            return Results.NotFound();
        }

        // Reconciliation asserts that an irreversible side effect did or did not happen, so it is
        // gated on the same scope as granting the approval that authorised the action.
        if (!identity.PermissionScopes.Contains(TaskPolicyEngine.ApprovalPermissionScope, StringComparer.Ordinal))
        {
            return Problem(
                StatusCodes.Status403Forbidden,
                ErrorCodes.PermissionScopeDenied,
                "Permission scope denied",
                $"Reconciling an external action requires the '{TaskPolicyEngine.ApprovalPermissionScope}' scope.");
        }

        var succeeded = string.Equals(reconciliation.Outcome, "succeeded", StringComparison.OrdinalIgnoreCase);
        var failed = string.Equals(reconciliation.Outcome, "failed", StringComparison.OrdinalIgnoreCase);
        if (!succeeded && !failed)
        {
            return Problem(
                StatusCodes.Status400BadRequest,
                ErrorCodes.InvalidTaskRequest,
                "Invalid reconciliation outcome",
                "Outcome must be 'succeeded' when the external action completed, or 'failed' when it did not.");
        }

        var unresolved = await externalActions.ListUnresolvedAsync(
            identity.TenantId,
            executionId,
            cancellationToken);
        var target = unresolved.FirstOrDefault(action =>
            string.Equals(action.ActionId, actionId, StringComparison.Ordinal));
        if (target is null)
        {
            return Results.NotFound();
        }

        var now = clock.GetUtcNow();
        var detail = string.IsNullOrWhiteSpace(reconciliation.Detail)
            ? $"Reconciled by '{identity.ActorId}'."
            : reconciliation.Detail;

        var record = succeeded
            ? await externalActions.CompleteAsync(
                identity.TenantId,
                target.IdempotencyReference,
                new ExternalActionResult(
                    JsonSerializer.SerializeToElement(new
                    {
                        reconciled = true,
                        reconciledBy = identity.ActorId,
                        detail
                    }),
                    [new EvidenceReference("reconciliation", $"actor:{identity.ActorId}", ObservedAt: now)]),
                now,
                cancellationToken)
            : await externalActions.FailAsync(
                identity.TenantId,
                target.IdempotencyReference,
                new Problem(
                    ErrorCodes.ExternalActionFailed,
                    "External action reconciled as failed",
                    detail,
                    Retryable: true),
                now,
                cancellationToken);

        await store.AppendEventAsync(
            executionId,
            ExecutionEventTypes.ExternalActionReconciled,
            now,
            JsonSerializer.SerializeToElement(new
            {
                actionId = target.ActionId,
                outcome = succeeded ? "succeeded" : "failed",
                reconciledBy = identity.ActorId
            }),
            cancellationToken);

        return Results.Ok(new UnresolvedExternalAction(
            record.ActionId,
            record.Capability,
            record.Status.ToString(),
            record.CreatedAt,
            record.UpdatedAt));
    }

}
