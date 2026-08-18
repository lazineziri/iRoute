using iRoute.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace iRoute.Data;

public sealed partial class PersistenceInitializer(
    IDbContextFactory<IRouteDbContext> contextFactory,
    IOptions<StorageOptions> storageOptions,
    StorageProvider storageProvider,
    IHostEnvironment environment,
    ILogger<PersistenceInitializer> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (!storageProvider.SupportsMultipleWorkers && !environment.IsDevelopment())
        {
            // Not fatal: a single-node self-host is a legitimate way to run iRoute. It is logged
            // because the limit is invisible until a second worker silently contends for the file.
            LogSingleNodeStorage(logger, storageProvider.Name, environment.EnvironmentName);
        }

        if (!storageOptions.Value.AutoInitialize)
        {
            return;
        }

        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        await context.Database.MigrateAsync(cancellationToken);
    }

    [LoggerMessage(
        1,
        LogLevel.Warning,
        "Storage:Provider={Provider} runs on one node only and cannot coordinate multiple execution " +
        "workers, but the environment is {Environment}. Use Postgres to deploy.")]
    private static partial void LogSingleNodeStorage(
        ILogger logger,
        string provider,
        string environment);

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
