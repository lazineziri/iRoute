using iRoute.Common;
using iRoute.Services;

namespace iRoute.Runtime.Composition;

internal static class RoutingServiceCollectionExtensions
{
    public static void AddRoutingServices(this IServiceCollection services)
    {
        services.AddSingleton<IEscalationPolicy, MeasuredEscalationPolicy>();
        services.AddSingleton<ICapabilityMatcher, MeasuredCapabilityMatcher>();
        services.AddSingleton<IDirectPathSelector, DirectPathSelector>();
        services.AddSingleton<IBoundedTaskPlanner, BoundedTaskPlanner>();
        services.AddSingleton<ITaskRouter, TaskRouter>();
        services.AddSingleton<IContextCompiler, BoundedContextCompiler>();
    }
}
