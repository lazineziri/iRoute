using iRoute.Common;
using Microsoft.Extensions.Options;

namespace iRoute.Runtime.Composition;

internal static class OptionsServiceCollectionExtensions
{
    public static void AddIRouteOptions(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.AddSingleton<IValidateOptions<ModelGatewayOptions>, ModelGatewayOptionsValidator>();
        services.AddOptions<ModelGatewayOptions>()
            .Bind(configuration.GetSection("ModelGateway"))
            .ValidateOnStart();
        services.AddSingleton(provider =>
            provider.GetRequiredService<IOptions<ModelGatewayOptions>>().Value.Resilience);

        services.AddSingleton<IValidateOptions<StorageOptions>, StorageOptionsValidator>();
        services.AddOptions<StorageOptions>()
            .Bind(configuration.GetSection("Storage"))
            .ValidateOnStart();

        services.AddSingleton<IValidateOptions<ObservabilityOptions>, ObservabilityOptionsValidator>();
        services.AddOptions<ObservabilityOptions>()
            .Bind(configuration.GetSection("Observability"))
            .ValidateOnStart();
        services.AddSingleton(provider =>
            provider.GetRequiredService<IOptions<ObservabilityOptions>>().Value);

        services.AddSingleton<IValidateOptions<LifecyclePolicy>, LifecyclePolicyValidator>();
        services.AddOptions<LifecyclePolicy>()
            .Bind(configuration.GetSection("Lifecycle"))
            .ValidateOnStart();
        services.AddSingleton(provider =>
            provider.GetRequiredService<IOptions<LifecyclePolicy>>().Value);
    }
}
