using System.Collections.Concurrent;
using iRoute.Common;
using Microsoft.Extensions.Options;

namespace iRoute.Services;

public sealed class ConfiguredGatewayDeploymentClientFactory(
    IHttpClientFactory httpClients,
    IOptions<ModelGatewayOptions> configuredOptions,
    TimeProvider clock) : IGatewayDeploymentClientFactory
{
    private readonly ModelGatewayOptions _options = configuredOptions.Value;
    private readonly ConcurrentDictionary<string, IModelGateway> _clients = new(StringComparer.Ordinal);

    public IModelGateway GetClient(GatewayDeployment deployment) =>
        _clients.GetOrAdd(deployment.RouteId, _ => CreateClient(deployment));

    private GenericHttpModelGateway CreateClient(GatewayDeployment deployment)
    {
        var route = ConfiguredGatewayDeploymentRegistry.EffectiveOptions(_options)
            .Single(item => string.Equals(item.RouteId, deployment.RouteId, StringComparison.Ordinal));
        var client = httpClients.CreateClient("iroute-generic-gateway");
        if (Uri.TryCreate(route.BaseUrl, UriKind.Absolute, out var baseUri))
        {
            client.BaseAddress = baseUri;
        }

        return new GenericHttpModelGateway(
            client,
            Options.Create(new ModelGatewayOptions
            {
                Mode = "Http",
                GatewayId = route.GatewayId,
                Transport = route.Transport,
                BaseUrl = route.BaseUrl,
                ApiKey = route.ApiKey,
                ExecutePath = route.ExecutePath,
                StreamPath = route.StreamPath,
                HealthPath = route.HealthPath
            }),
            clock);
    }
}
