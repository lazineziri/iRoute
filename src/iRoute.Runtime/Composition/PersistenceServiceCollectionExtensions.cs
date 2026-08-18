using iRoute.Common;
using iRoute.Data;
using Microsoft.EntityFrameworkCore;

namespace iRoute.Runtime.Composition;

internal static class PersistenceServiceCollectionExtensions
{
    public static void AddIRoutePersistence(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        var storageProvider = StorageProvider.Parse(configuration["Storage:Provider"]);
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
}
