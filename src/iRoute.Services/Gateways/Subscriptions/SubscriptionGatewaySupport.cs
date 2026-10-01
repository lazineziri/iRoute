using iRoute.Common;

namespace iRoute.Services;

internal static class SubscriptionGatewaySupport
{
    internal static void EnsureRequest(ModelGatewayRequest request, ModelGatewayDeploymentOptions route)
    {
        if (!string.Equals(request.TenantId, route.SubscriptionTenantId, StringComparison.Ordinal))
            throw new ModelGatewayException(ErrorCodes.PermissionScopeDenied,
                "This local subscription login belongs to a different configured tenant.", false,
                failureClass: GatewayFailureClass.Policy);
        if (request.MaximumCost is not null)
            throw new ModelGatewayException(ErrorCodes.CostBudgetExceeded,
                "Subscription quota is not API-dollar billing. Use an API route for a dollar cost ceiling.", false,
                failureClass: GatewayFailureClass.Policy);
        if (string.IsNullOrWhiteSpace(route.Model) || request.MaxOutputTokens <= 0 || request.DeadlineMilliseconds is <= 0)
            throw ProviderResponse.Invalid(request, "The subscription route requires a model and a positive output limit.");
    }

    internal static void EnsureSuccess(ProviderCliResult result)
    {
        if (result.ExitCode != 0)
            throw new ModelGatewayException(ErrorCodes.ModelGatewayUnavailable,
                "The provider CLI failed. Check its version, subscription login, model entitlement, and quota directly.", false,
                failureClass: GatewayFailureClass.Permanent);
    }
}
