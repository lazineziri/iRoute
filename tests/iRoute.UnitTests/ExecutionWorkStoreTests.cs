using iRoute.Contracts;
using iRoute.Core;
using iRoute.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace iRoute.UnitTests;

public sealed class ExecutionWorkStoreTests
{
    [Fact]
    public async Task ConcurrentWorkersClaimOnceAndLeaseTokenFencesThePreviousOwner()
    {
        var executions = new InMemoryExecutionStore();
        var work = new InMemoryExecutionWorkStore(executions);
        var now = DateTimeOffset.UtcNow;
        var snapshot = Snapshot(now);
        await executions.CreateAsync(snapshot, "queue-test", null,
            TestContext.Current.CancellationToken);
        var item = await work.EnqueueAsync(
            snapshot.ExecutionId,
            ExecutionStatus.Planning,
            now,
            TestContext.Current.CancellationToken);

        var claims = await Task.WhenAll(
            work.TryClaimAsync("worker-a", now, TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken),
            work.TryClaimAsync("worker-b", now, TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));

        var first = Assert.IsType<ExecutionLease>(Assert.Single(claims, claim => claim is not null));
        Assert.Equal(ExecutionWorkState.Pending, item.State);
        Assert.Equal(
            ExecutionStatus.Queued,
            Assert.IsType<ExecutionSnapshot>(await executions.GetAsync(
                snapshot.ExecutionId,
                TestContext.Current.CancellationToken)).Status);

        var takeover = Assert.IsType<ExecutionLease>(await work.TryClaimAsync(
            "worker-takeover",
            first.ExpiresAt.AddMilliseconds(1),
            TimeSpan.FromSeconds(10),
            TestContext.Current.CancellationToken));
        Assert.Equal(2, takeover.DeliveryAttempt);
        Assert.False(await work.CompleteAsync(first, now, TestContext.Current.CancellationToken));
        Assert.True(await work.CompleteAsync(takeover, now, TestContext.Current.CancellationToken));
        Assert.Equal(
            ExecutionWorkState.Completed,
            Assert.IsType<ExecutionWorkItem>(await work.GetAsync(
                snapshot.ExecutionId,
                TestContext.Current.CancellationToken)).State);
    }

    [Fact]
    public async Task SqliteQueueSurvivesRestartAndPropagatesCancellationThroughHeartbeat()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"iroute-work-{Guid.NewGuid():N}.db");
        try
        {
            var factory = new SqliteContextFactory(databasePath);
            await new SchemaMigrationManager(factory).UpgradeAsync(
                cancellationToken: TestContext.Current.CancellationToken);
            var executions = new EfExecutionStore(factory);
            var now = DateTimeOffset.UtcNow;
            var snapshot = Snapshot(now);
            await executions.CreateAsync(snapshot, "durable-queue-test", null,
            TestContext.Current.CancellationToken);
            await new EfExecutionWorkStore(factory).EnqueueAsync(
                snapshot.ExecutionId,
                ExecutionStatus.Planning,
                now,
                TestContext.Current.CancellationToken);

            var restarted = new EfExecutionWorkStore(factory);
            var lease = Assert.IsType<ExecutionLease>(await restarted.TryClaimAsync(
                "worker-after-restart",
                now,
                TimeSpan.FromSeconds(30),
                TestContext.Current.CancellationToken));
            await executions.UpdateAsync(
                snapshot with { Status = ExecutionStatus.Queued, UpdatedAt = now.AddSeconds(1) },
                TestContext.Current.CancellationToken);
            Assert.True(await executions.TryRequestCancellationAsync(
                snapshot.ExecutionId,
                now.AddSeconds(1),
                TestContext.Current.CancellationToken));

            var heartbeat = await restarted.RenewAsync(
                lease,
                now.AddSeconds(2),
                TimeSpan.FromSeconds(30),
                TestContext.Current.CancellationToken);
            Assert.True(heartbeat.Renewed);
            Assert.True(heartbeat.CancellationRequested);
            Assert.True(await restarted.AbandonAsync(
                lease,
                now.AddSeconds(3),
                TestContext.Current.CancellationToken));
            Assert.True(await restarted.CancelPendingAsync(
                snapshot.ExecutionId,
                now.AddSeconds(4),
                new Problem(
                    ErrorCodes.ExecutionCancelled,
                    "Execution cancelled",
                    "The execution was cancelled before it ran."),
                TestContext.Current.CancellationToken));
            Assert.Equal(
                ExecutionWorkState.Cancelled,
                Assert.IsType<ExecutionWorkItem>(await restarted.GetAsync(
                    snapshot.ExecutionId,
                    TestContext.Current.CancellationToken)).State);
            Assert.Equal(
                ExecutionStatus.Cancelled,
                Assert.IsType<ExecutionSnapshot>(await executions.GetAsync(
                    snapshot.ExecutionId,
                    TestContext.Current.CancellationToken)).Status);
        }
        finally
        {
            File.Delete(databasePath);
        }
    }

    private static ExecutionSnapshot Snapshot(DateTimeOffset now) => new(
        Guid.CreateVersion7(),
        "email.draft",
        ExecutionStatus.Planning,
        now,
        now,
        TenantId: "tenant-a",
        ActorId: "test-runner");

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
