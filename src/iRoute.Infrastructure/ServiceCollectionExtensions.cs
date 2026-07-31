using iRoute.Core;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace iRoute.Infrastructure;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddIRouteInfrastructure(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.Configure<ModelGatewayOptions>(configuration.GetSection("ModelGateway"));
        services.Configure<StorageOptions>(configuration.GetSection("Storage"));
        services.AddSingleton<IClock, SystemClock>();
        var storageProvider = configuration["Storage:Provider"] ?? "Sqlite";
        if (string.Equals(storageProvider, "Memory", StringComparison.OrdinalIgnoreCase))
        {
            services.AddSingleton<IExecutionStore, InMemoryExecutionStore>();
            services.AddSingleton<IWorkflowCheckpointStore, InMemoryWorkflowCheckpointStore>();
            services.AddSingleton<IArtifactStore, InMemoryArtifactStore>();
        }
        else
        {
            var connectionString = configuration.GetConnectionString("iRoute")
                ?? throw new InvalidOperationException("ConnectionStrings:iRoute is required for durable storage.");
            services.AddPooledDbContextFactory<IRouteDbContext>(options =>
            {
                if (string.Equals(storageProvider, "Sqlite", StringComparison.OrdinalIgnoreCase))
                {
                    options.UseSqlite(connectionString);
                }
                else if (string.Equals(storageProvider, "Postgres", StringComparison.OrdinalIgnoreCase))
                {
                    options.UseNpgsql(connectionString);
                }
                else
                {
                    throw new InvalidOperationException(
                        $"Unsupported storage provider '{storageProvider}'. Use Memory, Sqlite or Postgres.");
                }
            });
            services.AddSingleton<IExecutionStore, EfExecutionStore>();
            services.AddSingleton<IWorkflowCheckpointStore, EfWorkflowCheckpointStore>();
            services.AddSingleton<IArtifactStore, EfArtifactStore>();
            services.AddHostedService<PersistenceInitializer>();
            services.AddHealthChecks().AddCheck<DurableStorageHealthCheck>("storage", tags: ["ready"]);
        }

        services.AddSingleton<ITaskDefinitionRegistry, BuiltInTaskDefinitionRegistry>();
        services.AddSingleton<DeterministicModelGateway>();
        services.AddHttpClient<GenericHttpModelGateway>((provider, client) =>
        {
            var options = provider.GetRequiredService<Microsoft.Extensions.Options.IOptions<ModelGatewayOptions>>().Value;
            if (Uri.TryCreate(options.BaseUrl, UriKind.Absolute, out var baseUri))
            {
                client.BaseAddress = baseUri;
            }
        }).AddStandardResilienceHandler();
        services.AddTransient<IModelGateway>(provider =>
        {
            var options = provider.GetRequiredService<Microsoft.Extensions.Options.IOptions<ModelGatewayOptions>>().Value;
            return string.Equals(options.Mode, "Http", StringComparison.OrdinalIgnoreCase)
                ? provider.GetRequiredService<GenericHttpModelGateway>()
                : provider.GetRequiredService<DeterministicModelGateway>();
        });
        return services;
    }
}
