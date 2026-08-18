using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace iRoute.Data;

public sealed class DurableStorageHealthCheck(
    IDbContextFactory<IRouteDbContext> contextFactory) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await using var database = await contextFactory.CreateDbContextAsync(cancellationToken);
            if (!await database.Database.CanConnectAsync(cancellationToken))
            {
                return HealthCheckResult.Unhealthy("The durable store is not reachable.");
            }

            var pendingMigrations = await database.Database
                .GetPendingMigrationsAsync(cancellationToken);
            return pendingMigrations.Any()
                ? HealthCheckResult.Unhealthy(
                    "The durable store is reachable but requires schema migrations.")
                : HealthCheckResult.Healthy("The durable store is reachable and schema-current.");
        }
        catch (Exception exception)
        {
            return HealthCheckResult.Unhealthy("The durable store health check failed.", exception);
        }
    }
}
