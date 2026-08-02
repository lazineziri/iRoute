using iRoute.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace iRoute.UnitTests;

public sealed class SchemaMigrationTests
{
    [Fact]
    public async Task UpgradeRollbackAndReapplyUseExplicitMigrationTargets()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"iroute-migrations-{Guid.NewGuid():N}.db");
        try
        {
            var manager = new SchemaMigrationManager(new SqliteContextFactory(databasePath));
            var initial = await manager.GetStatusAsync(TestContext.Current.CancellationToken);
            Assert.Null(initial.CurrentMigration);
            Assert.NotEmpty(initial.PendingMigrations);

            var upgraded = await manager.UpgradeAsync(
                cancellationToken: TestContext.Current.CancellationToken);
            Assert.True(upgraded.IsCurrent);
            Assert.Equal(
                iRoute.Infrastructure.Migrations.GatewayCircuitBreaker.MigrationId,
                upgraded.CurrentMigration);

            await Assert.ThrowsAsync<InvalidOperationException>(() => manager.RollbackAsync(
                iRoute.Infrastructure.Migrations.RoutingDecisionCheckpoint.MigrationId,
                false,
                TestContext.Current.CancellationToken));

            var rolledBack = await manager.RollbackAsync(
                iRoute.Infrastructure.Migrations.RoutingDecisionCheckpoint.MigrationId,
                true,
                TestContext.Current.CancellationToken);
            Assert.Equal(
                iRoute.Infrastructure.Migrations.RoutingDecisionCheckpoint.MigrationId,
                rolledBack.CurrentMigration);
            Assert.Equal(
                [
                    iRoute.Infrastructure.Migrations.LifecycleCleanup.MigrationId,
                    iRoute.Infrastructure.Migrations.DurableExecutionWork.MigrationId,
                    iRoute.Infrastructure.Migrations.GatewayCircuitBreaker.MigrationId
                ],
                rolledBack.PendingMigrations);

            var reapplied = await manager.UpgradeAsync(
                cancellationToken: TestContext.Current.CancellationToken);
            Assert.True(reapplied.IsCurrent);
        }
        finally
        {
            File.Delete(databasePath);
        }
    }

    [Fact]
    public async Task ReadinessRejectsPendingSchemaAndAcceptsCurrentSchema()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"iroute-readiness-{Guid.NewGuid():N}.db");
        try
        {
            var factory = new SqliteContextFactory(databasePath);
            var health = new DurableStorageHealthCheck(factory);
            var before = await health.CheckHealthAsync(
                new HealthCheckContext(),
                TestContext.Current.CancellationToken);
            Assert.Equal(HealthStatus.Unhealthy, before.Status);

            await new SchemaMigrationManager(factory).UpgradeAsync(
                cancellationToken: TestContext.Current.CancellationToken);
            var after = await health.CheckHealthAsync(
                new HealthCheckContext(),
                TestContext.Current.CancellationToken);
            Assert.Equal(HealthStatus.Healthy, after.Status);
        }
        finally
        {
            File.Delete(databasePath);
        }
    }

    private sealed class SqliteContextFactory(string databasePath)
        : IDbContextFactory<IRouteDbContext>
    {
        private readonly DbContextOptions<IRouteDbContext> _options =
            new DbContextOptionsBuilder<IRouteDbContext>()
                .UseSqlite($"Data Source={databasePath}")
                .ConfigureWarnings(warnings => warnings.Ignore(RelationalEventId.PendingModelChangesWarning))
                .Options;

        public IRouteDbContext CreateDbContext() => new(_options);
    }
}
