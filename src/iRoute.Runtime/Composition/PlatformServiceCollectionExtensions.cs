namespace iRoute.Runtime.Composition;

public static class PlatformServiceCollectionExtensions
{
    public static IServiceCollection AddIRoutePlatform(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.AddIRouteOptions(configuration);
        services.AddSingleton(TimeProvider.System);
        services.AddIRoutePersistence(configuration);
        services.AddIRouteCapabilities();
        services.AddIRouteModelGateways();
        return services;
    }
}
