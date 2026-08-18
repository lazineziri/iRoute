using iRoute.Common;
using iRoute.Services;
using Microsoft.Extensions.Options;

namespace iRoute.Runtime.Composition;

internal static class GatewayServiceCollectionExtensions
{
    public static void AddIRouteModelGateways(this IServiceCollection services)
    {
        services.AddSingleton<DeterministicModelGateway>();
        services.AddSingleton<IGatewayDeploymentRegistry, ConfiguredGatewayDeploymentRegistry>();
        services.AddSingleton<IGatewayDeploymentClientFactory, ConfiguredGatewayDeploymentClientFactory>();
        services.AddHttpClient("iroute-generic-gateway");
        services.AddHttpClient<GenericHttpModelGateway>((provider, client) =>
        {
            var options = provider.GetRequiredService<IOptions<ModelGatewayOptions>>().Value;
            if (Uri.TryCreate(options.BaseUrl, UriKind.Absolute, out var baseUri))
            {
                client.BaseAddress = baseUri;
            }
        });
        services.AddTransient<ResilientModelGateway>();
        services.AddTransient<IModelGateway>(provider =>
        {
            var options = provider.GetRequiredService<IOptions<ModelGatewayOptions>>().Value;
            if (!string.Equals(options.Mode, "Http", StringComparison.OrdinalIgnoreCase))
            {
                return provider.GetRequiredService<DeterministicModelGateway>();
            }

            return options.Resilience.Enabled
                ? provider.GetRequiredService<ResilientModelGateway>()
                : provider.GetRequiredService<GenericHttpModelGateway>();
        });
        services.AddHealthChecks().AddCheck<ModelGatewayHealthCheck>(
            "model_gateway",
            tags: ["gateway"]);
    }
}
