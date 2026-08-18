using Microsoft.Extensions.Options;

namespace iRoute.Runtime.Hosting;

public static class BackgroundWorkerServiceCollectionExtensions
{
    public static IServiceCollection AddIRouteBackgroundWorkers(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.AddSingleton<IValidateOptions<ExecutionWorkerOptions>, ExecutionWorkerOptionsValidator>();
        services.AddOptions<ExecutionWorkerOptions>()
            .BindConfiguration("ExecutionWorker")
            .ValidateOnStart();

        if (configuration.GetValue("ExecutionWorker:Enabled", true))
        {
            services.AddHostedService<ExecutionWorker>();
        }

        if (configuration.GetValue("Lifecycle:Enabled", true))
        {
            services.AddHostedService<LifecycleWorker>();
        }

        return services;
    }
}
