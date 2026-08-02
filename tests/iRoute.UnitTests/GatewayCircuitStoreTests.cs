using iRoute.Contracts;
using iRoute.Core;
using iRoute.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace iRoute.UnitTests;

public sealed class GatewayCircuitStoreTests
{
    private static readonly GatewayCircuitPolicy Policy = new(
        1,
        TimeSpan.FromSeconds(1),
        TimeSpan.FromMinutes(1),
        TimeSpan.FromSeconds(5));

    [Fact]
    public async Task SqliteCircuitStateSurvivesStoreReconstruction()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"iroute-circuits-{Guid.NewGuid():N}.db");
        try
        {
            var factory = new SqliteContextFactory(databasePath);
            await new SchemaMigrationManager(factory).UpgradeAsync(
                cancellationToken: TestContext.Current.CancellationToken);
            var now = new DateTimeOffset(2026, 8, 2, 12, 0, 0, TimeSpan.Zero);
            var first = new EfGatewayCircuitStore(factory);
            var permit = await first.TryAcquireAsync(
                "deployment-a",
                "worker-a",
                Policy,
                now,
                TestContext.Current.CancellationToken);
            _ = await first.RecordFailureAsync(
                permit,
                GatewayFailureClass.Throttling,
                true,
                TimeSpan.FromSeconds(30),
                Policy,
                now,
                TestContext.Current.CancellationToken);

            var reconstructed = new EfGatewayCircuitStore(new SqliteContextFactory(databasePath));
            var snapshot = Assert.Single(await reconstructed.ListAsync(
                TestContext.Current.CancellationToken));

            Assert.Equal(GatewayCircuitState.Open, snapshot.State);
            Assert.Equal(GatewayFailureClass.Throttling, snapshot.LastFailureClass);
            Assert.Equal(now.AddSeconds(30), snapshot.NextProbeAt);
        }
        finally
        {
            File.Delete(databasePath);
        }
    }

    [Fact]
    public async Task ExpiredProbeLeaseIsFencedFromStaleCompletion()
    {
        var store = new InMemoryGatewayCircuitStore();
        var now = new DateTimeOffset(2026, 8, 2, 12, 0, 0, TimeSpan.Zero);
        var closed = await store.TryAcquireAsync(
            "deployment-a",
            "worker-a",
            Policy,
            now,
            TestContext.Current.CancellationToken);
        _ = await store.RecordFailureAsync(
            closed,
            GatewayFailureClass.Provider,
            true,
            null,
            Policy,
            now,
            TestContext.Current.CancellationToken);
        var stale = await store.TryAcquireAsync(
            "deployment-a",
            "worker-a",
            Policy,
            now.AddSeconds(2),
            TestContext.Current.CancellationToken);
        var takeover = await store.TryAcquireAsync(
            "deployment-a",
            "worker-b",
            Policy,
            now.AddSeconds(8),
            TestContext.Current.CancellationToken);

        Assert.True(stale.Granted);
        Assert.True(takeover.Granted);
        Assert.NotEqual(stale.ProbeToken, takeover.ProbeToken);
        var afterStaleSuccess = await store.RecordSuccessAsync(
            stale,
            now.AddSeconds(9),
            TestContext.Current.CancellationToken);
        Assert.Equal(GatewayCircuitState.HalfOpen, afterStaleSuccess.State);
        Assert.Equal(takeover.ProbeToken, afterStaleSuccess.ProbeToken);

        var recovered = await store.RecordSuccessAsync(
            takeover,
            now.AddSeconds(9),
            TestContext.Current.CancellationToken);
        Assert.Equal(GatewayCircuitState.Closed, recovered.State);
    }

    [Fact]
    public async Task PostgresReplicasGrantOnlyOneHalfOpenProbe()
    {
        var connectionString = Environment.GetEnvironmentVariable("IROUTE_TEST_POSTGRES");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            return;
        }

        var factory = new PostgresContextFactory(connectionString);
        await new SchemaMigrationManager(factory).UpgradeAsync(
            cancellationToken: TestContext.Current.CancellationToken);
        var deploymentId = $"w18-probe-{Guid.NewGuid():N}";
        var now = new DateTimeOffset(2026, 8, 2, 12, 0, 0, TimeSpan.Zero);
        try
        {
            var initialStore = new EfGatewayCircuitStore(factory);
            var closed = await initialStore.TryAcquireAsync(
                deploymentId,
                "opening-worker",
                Policy,
                now,
                TestContext.Current.CancellationToken);
            _ = await initialStore.RecordFailureAsync(
                closed,
                GatewayFailureClass.Provider,
                true,
                null,
                Policy,
                now,
                TestContext.Current.CancellationToken);

            var probes = await Task.WhenAll(Enumerable.Range(0, 16).Select(index =>
                new EfGatewayCircuitStore(new PostgresContextFactory(connectionString)).TryAcquireAsync(
                    deploymentId,
                    $"worker-{index}",
                    Policy,
                    now.AddSeconds(2),
                    TestContext.Current.CancellationToken)));

            var granted = Assert.Single(probes, item => item.Granted);
            Assert.Equal(GatewayCircuitState.HalfOpen, granted.State);
            Assert.All(probes.Where(item => !item.Granted), item =>
                Assert.Equal(GatewayCircuitState.HalfOpen, item.State));
        }
        finally
        {
            await using var cleanup = factory.CreateDbContext();
            var entity = await cleanup.GatewayCircuits.FindAsync(
                [deploymentId],
                TestContext.Current.CancellationToken);
            if (entity is not null)
            {
                cleanup.GatewayCircuits.Remove(entity);
                await cleanup.SaveChangesAsync(TestContext.Current.CancellationToken);
            }
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

    private sealed class PostgresContextFactory(string connectionString)
        : IDbContextFactory<IRouteDbContext>
    {
        private readonly DbContextOptions<IRouteDbContext> _options =
            new DbContextOptionsBuilder<IRouteDbContext>()
                .UseNpgsql(connectionString)
                .Options;

        public IRouteDbContext CreateDbContext() => new(_options);
    }
}
