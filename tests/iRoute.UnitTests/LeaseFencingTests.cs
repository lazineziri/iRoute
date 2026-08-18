using iRoute.Contracts;
using iRoute.Core;
using iRoute.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using System.Text.Json;

namespace iRoute.UnitTests;

/// <summary>
/// A worker whose lease expired can still be mid-execution when another worker takes over. The
/// lease token fences the work-item row, but execution state, events and checkpoints were written
/// without it, so a stale worker could interleave writes with the new owner.
/// </summary>
public sealed class LeaseFencingTests
{
    [Fact]
    public async Task AStaleWorkerCannotWriteExecutionStateAfterTakeover()
    {
        await using var harness = await Harness.CreateAsync();
        var stale = await harness.ClaimAsync("worker-stale");
        var current = await harness.TakeOverAsync("worker-current");

        Assert.NotEqual(stale.LeaseToken, current.LeaseToken);

        using (harness.Fence.Hold(current.LeaseToken))
        {
            await harness.Executions.UpdateAsync(
                harness.Snapshot with { Status = ExecutionStatus.Running },
                TestContext.Current.CancellationToken);
        }

        using (harness.Fence.Hold(stale.LeaseToken))
        {
            await Assert.ThrowsAsync<LeaseFencedException>(() => harness.Executions.UpdateAsync(
                harness.Snapshot with { Status = ExecutionStatus.Failed },
                TestContext.Current.CancellationToken));
        }

        var persisted = Assert.IsType<ExecutionSnapshot>(await harness.Executions.GetAsync(
            harness.Snapshot.ExecutionId,
            TestContext.Current.CancellationToken));
        Assert.Equal(ExecutionStatus.Running, persisted.Status);
    }

    [Fact]
    public async Task AStaleWorkerCannotAppendEventsAfterTakeover()
    {
        await using var harness = await Harness.CreateAsync();
        var stale = await harness.ClaimAsync("worker-stale");
        await harness.TakeOverAsync("worker-current");

        using (harness.Fence.Hold(stale.LeaseToken))
        {
            await Assert.ThrowsAsync<LeaseFencedException>(() => harness.Executions.AppendEventAsync(
                harness.Snapshot.ExecutionId,
                ExecutionEventTypes.StepStarted,
                DateTimeOffset.UtcNow,
                System.Text.Json.JsonSerializer.SerializeToElement(new { from = "stale" }),
                TestContext.Current.CancellationToken));
        }
    }

    [Fact]
    public async Task AStaleWorkerCannotMutateWorkflowCheckpointsAfterTakeover()
    {
        await using var harness = await Harness.CreateAsync();
        var stale = await harness.ClaimAsync("worker-stale");

        using (harness.Fence.Hold(stale.LeaseToken))
        {
            await harness.Checkpoints.StartStepAsync(
                harness.Snapshot.ExecutionId,
                "step-a",
                DateTimeOffset.UtcNow,
                TestContext.Current.CancellationToken);
        }

        var current = await harness.TakeOverAsync("worker-current");
        Assert.NotEqual(stale.LeaseToken, current.LeaseToken);

        using (harness.Fence.Hold(stale.LeaseToken))
        {
            await Assert.ThrowsAsync<LeaseFencedException>(() => harness.Checkpoints.CompleteStepAsync(
                harness.Snapshot.ExecutionId,
                "step-a",
                JsonSerializer.SerializeToElement(new { from = "stale" }),
                DateTimeOffset.UtcNow,
                TestContext.Current.CancellationToken));
        }

        var checkpoint = Assert.IsType<WorkflowCheckpoint>(await harness.Checkpoints.GetAsync(
            harness.Snapshot.ExecutionId,
            TestContext.Current.CancellationToken));
        Assert.Equal(WorkflowStepStatus.Running, Assert.Single(checkpoint.Steps).Status);
    }

    [Fact]
    public async Task WritesOutsideAnyLeaseAreUnaffected()
    {
        // The synchronous execution path holds no lease, so an unheld fence must not block it.
        await using var harness = await Harness.CreateAsync();

        await harness.Executions.UpdateAsync(
            harness.Snapshot with { Status = ExecutionStatus.Resolving },
            TestContext.Current.CancellationToken);

        var persisted = Assert.IsType<ExecutionSnapshot>(await harness.Executions.GetAsync(
            harness.Snapshot.ExecutionId,
            TestContext.Current.CancellationToken));
        Assert.Equal(ExecutionStatus.Resolving, persisted.Status);
    }

    [Fact]
    public async Task TheCurrentOwnerWritesNormally()
    {
        await using var harness = await Harness.CreateAsync();
        var lease = await harness.ClaimAsync("worker-current");

        using (harness.Fence.Hold(lease.LeaseToken))
        {
            await harness.Executions.UpdateAsync(
                harness.Snapshot with { Status = ExecutionStatus.Running },
                TestContext.Current.CancellationToken);
            await harness.Executions.AppendEventAsync(
                harness.Snapshot.ExecutionId,
                ExecutionEventTypes.StepStarted,
                DateTimeOffset.UtcNow,
                System.Text.Json.JsonSerializer.SerializeToElement(new { from = "owner" }),
                TestContext.Current.CancellationToken);
        }

        var persisted = Assert.IsType<ExecutionSnapshot>(await harness.Executions.GetAsync(
            harness.Snapshot.ExecutionId,
            TestContext.Current.CancellationToken));
        Assert.Equal(ExecutionStatus.Running, persisted.Status);
    }

    private sealed class Harness : IAsyncDisposable
    {
        private readonly string _databasePath;

        private Harness(
            string databasePath,
            EfExecutionStore executions,
            EfExecutionWorkStore work,
            EfWorkflowCheckpointStore checkpoints,
            AsyncLocalExecutionFence fence,
            ExecutionSnapshot snapshot)
        {
            _databasePath = databasePath;
            Executions = executions;
            Work = work;
            Checkpoints = checkpoints;
            Fence = fence;
            Snapshot = snapshot;
        }

        public EfExecutionStore Executions { get; }
        public EfExecutionWorkStore Work { get; }
        public EfWorkflowCheckpointStore Checkpoints { get; }
        public AsyncLocalExecutionFence Fence { get; }
        public ExecutionSnapshot Snapshot { get; }

        public static async Task<Harness> CreateAsync()
        {
            var databasePath = Path.Combine(Path.GetTempPath(), $"iroute-fence-{Guid.NewGuid():N}.db");
            var factory = new SqliteContextFactory(databasePath);
            await new SchemaMigrationManager(factory).UpgradeAsync(
                cancellationToken: TestContext.Current.CancellationToken);

            var fence = new AsyncLocalExecutionFence();
            var executions = new EfExecutionStore(factory, fence);
            var work = new EfExecutionWorkStore(factory);
            var checkpoints = new EfWorkflowCheckpointStore(factory, fence);
            var now = DateTimeOffset.UtcNow;
            var snapshot = new ExecutionSnapshot(
                Guid.CreateVersion7(),
                "email.draft",
                ExecutionStatus.Planning,
                now,
                now,
                TenantId: "tenant-a",
                ActorId: "test-runner");
            await executions.CreateAsync(snapshot, "fence-test", null, TestContext.Current.CancellationToken);
            var request = new TaskRequest(
                "test.workflow",
                JsonSerializer.SerializeToElement(new { value = "input" }),
                TenantId: "tenant-a",
                ActorId: "test-runner");
            var plan = new ExecutionPlan(
                "test.workflow@1",
                1,
                "test.workflow",
                1,
                [new ExecutionPlanStep(
                    "step-a",
                    ExecutionStepKind.Deterministic,
                    "test.step-a",
                    [],
                    SideEffectClass.None,
                    5_000)],
                new ExecutionPlanBudget(1, 0, 0, 1, 1, 30_000));
            var routing = new RoutingDecision(
                "test.v1",
                RoutingPath.Workflow,
                "Test workflow route.",
                "test.step-a",
                null,
                null,
                1m,
                1m,
                0m,
                1,
                0m,
                1m,
                true,
                1,
                false,
                null,
                []);
            await checkpoints.InitializeAsync(
                snapshot.ExecutionId,
                request,
                plan,
                routing,
                now,
                TestContext.Current.CancellationToken);
            await work.EnqueueAsync(
                snapshot.ExecutionId,
                ExecutionStatus.Planning,
                now,
                TestContext.Current.CancellationToken);
            return new Harness(databasePath, executions, work, checkpoints, fence, snapshot);
        }

        public async Task<ExecutionLease> ClaimAsync(string workerId) =>
            Assert.IsType<ExecutionLease>(await Work.TryClaimAsync(
                workerId,
                DateTimeOffset.UtcNow,
                TimeSpan.FromSeconds(30),
                TestContext.Current.CancellationToken));

        public async Task<ExecutionLease> TakeOverAsync(string workerId) =>
            Assert.IsType<ExecutionLease>(await Work.TryClaimAsync(
                workerId,
                DateTimeOffset.UtcNow.AddMinutes(5),
                TimeSpan.FromSeconds(30),
                TestContext.Current.CancellationToken));

        public ValueTask DisposeAsync()
        {
            if (File.Exists(_databasePath)) File.Delete(_databasePath);
            return ValueTask.CompletedTask;
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
