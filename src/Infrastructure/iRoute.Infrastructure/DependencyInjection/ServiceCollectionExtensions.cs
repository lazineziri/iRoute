using iRoute.Core;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace iRoute.Infrastructure;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddIRouteInfrastructure(
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
        services.AddSingleton(TimeProvider.System);
        var storageProvider = StorageProvider.Parse(configuration["Storage:Provider"]);
        {
            var connectionString = SqliteStoragePath.ResolveForProvider(
                storageProvider.Name,
                configuration.GetConnectionString("iRoute")
                    ?? throw new InvalidOperationException("ConnectionStrings:iRoute is required for durable storage."));
            services.AddPooledDbContextFactory<IRouteDbContext>(options =>
            {
                if (storageProvider.IsSqlite)
                {
                    options.UseSqlite(connectionString);
                }
                else
                {
                    options.UseNpgsql(connectionString);
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
            services.AddSingleton<IGatewayCircuitStore, EfGatewayCircuitStore>();
            services.AddSingleton(storageProvider);
            services.AddSingleton<IExecutionFence, AsyncLocalExecutionFence>();
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
        services.AddSingleton<IGatewayDeploymentRegistry, ConfiguredGatewayDeploymentRegistry>();
        services.AddSingleton<IGatewayDeploymentClientFactory, ConfiguredGatewayDeploymentClientFactory>();
        services.AddHttpClient("iroute-generic-gateway");
        services.AddHttpClient<GenericHttpModelGateway>((provider, client) =>
        {
            var options = provider.GetRequiredService<Microsoft.Extensions.Options.IOptions<ModelGatewayOptions>>().Value;
            if (Uri.TryCreate(options.BaseUrl, UriKind.Absolute, out var baseUri))
            {
                client.BaseAddress = baseUri;
            }
        });
        services.AddTransient<ResilientModelGateway>();
        services.AddTransient<IModelGateway>(provider =>
        {
            var options = provider.GetRequiredService<Microsoft.Extensions.Options.IOptions<ModelGatewayOptions>>().Value;
            if (!string.Equals(options.Mode, "Http", StringComparison.OrdinalIgnoreCase))
            {
                return provider.GetRequiredService<DeterministicModelGateway>();
            }

            return options.Resilience.Enabled
                ? provider.GetRequiredService<ResilientModelGateway>()
                : provider.GetRequiredService<GenericHttpModelGateway>();
        });
        services.AddHealthChecks().AddCheck<ModelGatewayHealthCheck>(
            "model_gateway",
            tags: ["gateway"]);
        return services;
    }
}
