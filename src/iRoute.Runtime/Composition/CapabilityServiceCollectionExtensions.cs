using iRoute.Common;
using iRoute.Services;

namespace iRoute.Runtime.Composition;

internal static class CapabilityServiceCollectionExtensions
{
    public static void AddIRouteCapabilities(this IServiceCollection services)
    {
        services.AddSingleton<ITaskDefinitionRegistry, BuiltInTaskDefinitionRegistry>();
        services.AddSingleton<IModelProfileRegistry, BuiltInModelProfileRegistry>();
        services.AddSingleton<ICapabilityDefinitionRegistry, BuiltInCapabilityDefinitionRegistry>();
        services.AddSingleton<ICapabilityConnector, ReferenceEmailConnector>();
        services.AddSingleton<ICapabilityConnector, ReferenceCalendarConnector>();
        services.AddSingleton<ICapabilityConnector, ReferenceDatabaseConnector>();
        services.AddSingleton<ICapabilityConnector, ReferenceOpenApiConnector>();
        services.AddSingleton<ICapabilityConnector, ReferenceMcpConnector>();
        services.AddSingleton<ICapabilityConnector, ReferenceAgentResultConnector>();
        services.AddSingleton<ICapabilityExecutor, NormalizedCapabilityExecutor>();
        services.AddSingleton<IExternalActionExecutor, CapabilityExternalActionExecutor>();
    }
}
