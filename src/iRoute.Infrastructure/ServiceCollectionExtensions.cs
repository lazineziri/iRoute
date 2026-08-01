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
        var observabilityOptions = configuration.GetSection("Observability").Get<ObservabilityOptions>()
            ?? new ObservabilityOptions();
        observabilityOptions.EnsureValid();
        services.AddSingleton(observabilityOptions);
        var lifecyclePolicy = configuration.GetSection("Lifecycle").Get<LifecyclePolicy>()
            ?? new LifecyclePolicy();
        lifecyclePolicy.EnsureValid();
        services.AddSingleton(lifecyclePolicy);
        services.AddSingleton<IClock, SystemClock>();
        var storageProvider = configuration["Storage:Provider"] ?? "Sqlite";
        if (string.Equals(storageProvider, "Memory", StringComparison.OrdinalIgnoreCase))
        {
            services.AddSingleton<InMemoryExecutionStore>();
            services.AddSingleton<IExecutionStore>(provider =>
                provider.GetRequiredService<InMemoryExecutionStore>());
            services.AddSingleton<IExecutionWorkStore, InMemoryExecutionWorkStore>();
            services.AddSingleton<IWorkflowCheckpointStore, InMemoryWorkflowCheckpointStore>();
            services.AddSingleton<IApprovalStore, InMemoryApprovalStore>();
            services.AddSingleton<IExternalActionStore, InMemoryExternalActionStore>();
            services.AddSingleton<InMemoryArtifactStore>();
            services.AddSingleton<IArtifactStore>(provider =>
                provider.GetRequiredService<InMemoryArtifactStore>());
            services.AddSingleton<InMemoryMemoryStore>();
            services.AddSingleton<IMemoryStore>(provider =>
                provider.GetRequiredService<InMemoryMemoryStore>());
            services.AddSingleton<ILifecycleStore, InMemoryLifecycleStore>();
            services.AddSingleton<IObservabilityStore, InMemoryObservabilityStore>();
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
            services.AddSingleton<IExecutionWorkStore, EfExecutionWorkStore>();
            services.AddSingleton<IWorkflowCheckpointStore, EfWorkflowCheckpointStore>();
            services.AddSingleton<IApprovalStore, EfApprovalStore>();
            services.AddSingleton<IExternalActionStore, EfExternalActionStore>();
            services.AddSingleton<IArtifactStore, EfArtifactStore>();
            services.AddSingleton<IMemoryStore, EfMemoryStore>();
            services.AddSingleton<ILifecycleStore, EfLifecycleStore>();
            services.AddSingleton<IObservabilityStore, EfObservabilityStore>();
            services.AddSingleton<SchemaMigrationManager>();
            services.AddHostedService<PersistenceInitializer>();
            services.AddHealthChecks().AddCheck<DurableStorageHealthCheck>("storage", tags: ["ready"]);
        }

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
        services.AddHealthChecks().AddCheck<ModelGatewayHealthCheck>(
            "model_gateway",
            tags: ["gateway"]);
        return services;
    }
}
