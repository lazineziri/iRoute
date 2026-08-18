using iRoute.Common;

namespace iRoute.Runtime.Composition;

public static class RuntimeServiceCollectionExtensions
{
    public static IServiceCollection AddIRouteRuntime(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.AddOptions<WorkflowSchedulerOptions>()
            .Bind(configuration.GetSection("Workflow"))
            .ValidateOnStart();
        services.AddExecutionServices();
        services.AddRoutingServices();
        services.AddResolutionServices();
        return services;
    }
}
