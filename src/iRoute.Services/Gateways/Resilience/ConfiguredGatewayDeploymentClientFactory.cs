using System.Collections.Concurrent;
using iRoute.Common;
using Microsoft.Extensions.Options;

namespace iRoute.Services;

public sealed class ConfiguredGatewayDeploymentClientFactory(
    IHttpClientFactory httpClients,
    IProviderCliRunner cli,
    IOptions<ModelGatewayOptions> configuredOptions,
    TimeProvider clock,
    IChatGPTAccessTokenProvider chatGPTTokens,
    ITenantQuotaStore quotas,
    TenantQuotaOptions quotaOptions) : IGatewayDeploymentClientFactory
{
    private readonly ModelGatewayOptions _options = configuredOptions.Value;
    private readonly ConcurrentDictionary<string, IModelGateway> _clients = new(StringComparer.Ordinal);

    public IModelGateway GetClient(GatewayDeployment deployment) =>
        _clients.GetOrAdd(deployment.RouteId, _ => CreateClient(deployment));

    public IModelGateway GetDefaultClient() =>
        GetClient(ConfiguredGatewayDeploymentRegistry.ToDeployment(
            ConfiguredGatewayDeploymentRegistry.EffectiveOptions(_options).First(item => item.Enabled)));

    private IModelGateway CreateClient(GatewayDeployment deployment)
    {
        var route = ConfiguredGatewayDeploymentRegistry.EffectiveOptions(_options)
            .Single(item => string.Equals(item.RouteId, deployment.RouteId, StringComparison.Ordinal));
        var native = CreateNativeClient(route);
        return quotaOptions.Enabled ? new QuotaModelGateway(native, quotas, quotaOptions, route, clock) : native;
    }

    private IModelGateway CreateNativeClient(ModelGatewayDeploymentOptions route)
    {
        if (route.Adapter.Equals("ClaudeCode", StringComparison.OrdinalIgnoreCase))
            return new ClaudeCodeSubscriptionGateway(cli, route);
        var client = httpClients.CreateClient("iroute-generic-gateway");
        var transport = new ProviderHttpTransport(client, route, clock, chatGPTTokens);
        if (route.Adapter.Equals("OpenAI", StringComparison.OrdinalIgnoreCase) ||
            route.Adapter.Equals("OpenAIChatGPT", StringComparison.OrdinalIgnoreCase))
            return new OpenAIModelGateway(transport, route);
        if (route.Adapter.Equals("Anthropic", StringComparison.OrdinalIgnoreCase))
            return new AnthropicModelGateway(transport, route);
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
