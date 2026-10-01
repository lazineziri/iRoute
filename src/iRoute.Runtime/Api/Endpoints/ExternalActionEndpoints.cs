using iRoute.Common;
using iRoute.Core;
using Microsoft.Extensions.Options;
using static iRoute.Runtime.Api.ExecutionEndpointSupport;

namespace iRoute.Runtime.Api;

internal static class ExternalActionEndpoints
{
    internal static async Task<IResult> ListUnresolvedActionsAsync(
        Guid executionId, HttpRequest request, IOptions<IRouteIdentityOptions> identityOptions,
        ExecutionOrchestrator orchestrator, CancellationToken cancellationToken)
    {
        var identity = RequestIdentity.Resolve(request, identityOptions.Value);
        try
        {
            var result = await orchestrator.ListUnresolvedActionsAsync(
                executionId, identity.TenantId, identity.PermissionScopes, cancellationToken);
            return result is null ? Results.NotFound() : Results.Ok(result);
        }
        catch (ExecutionCommandException exception)
        {
            return CommandProblem(exception);
        }
    }

    internal static async Task<IResult> ReconcileActionAsync(
        Guid executionId, string actionId, ExternalActionReconciliation reconciliation,
        HttpRequest request, IOptions<IRouteIdentityOptions> identityOptions,
        ExecutionOrchestrator orchestrator, CancellationToken cancellationToken)
    {
        var identity = RequestIdentity.Resolve(request, identityOptions.Value);
        try
        {
            var result = await orchestrator.ReconcileActionAsync(
                executionId, actionId, reconciliation, identity.TenantId, identity.ActorId,
                identity.PermissionScopes, cancellationToken);
            return result is null ? Results.NotFound() : Results.Ok(result);
        }
        catch (ExecutionCommandException exception)
        {
            return CommandProblem(exception);
        }
    }
}
