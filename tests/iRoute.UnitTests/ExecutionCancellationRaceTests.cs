using iRoute.Contracts;
using iRoute.Core;
using iRoute.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace iRoute.UnitTests;

/// <summary>
/// The API host and the worker host are separate processes sharing only the database, so a
/// cancellation request and a worker state transition interleave in practice. Neither may
/// silently discard the other's write.
/// </summary>
public sealed class ExecutionCancellationRaceTests
{
    public static TheoryData<string> StoreKinds() => new() { "memory", "sqlite" };

    [Theory]
    [MemberData(nameof(StoreKinds))]
    public async Task CancellationSurvivesAWorkerWriteThatStartedBeforeIt(string storeKind)
    {
        await using var harness = await StoreHarness.CreateAsync(storeKind);
        var store = harness.Store;
        var now = DateTimeOffset.UtcNow;
        var snapshot = Snapshot(now);
        await store.CreateAsync(snapshot, "cancel-race-1", null,
            TestContext.Current.CancellationToken);

        // The worker reads the execution and begins a phase.
        var workerCopy = Assert.IsType<ExecutionSnapshot>(
            await store.GetAsync(snapshot.ExecutionId, TestContext.Current.CancellationToken));

        // A cancellation arrives while that phase is still running.
        var requestedAt = now.AddSeconds(1);
        Assert.True(await store.TryRequestCancellationAsync(
            snapshot.ExecutionId,
            requestedAt,
            TestContext.Current.CancellationToken));

        // The worker now persists its transition from the copy it read earlier.
        await store.UpdateAsync(
            workerCopy with { Status = ExecutionStatus.Resolving, UpdatedAt = now.AddSeconds(2) },
            TestContext.Current.CancellationToken);

        var persisted = Assert.IsType<ExecutionSnapshot>(
            await store.GetAsync(snapshot.ExecutionId, TestContext.Current.CancellationToken));
        Assert.Equal(ExecutionStatus.Resolving, persisted.Status);
        Assert.Equal(
            requestedAt.ToUnixTimeMilliseconds(),
            persisted.CancellationRequestedAt?.ToUnixTimeMilliseconds());
    }

    [Theory]
    [MemberData(nameof(StoreKinds))]
    public async Task CancellingAFinishedExecutionCannotEraseItsOutcome(string storeKind)
    {
        await using var harness = await StoreHarness.CreateAsync(storeKind);
        var store = harness.Store;
        var now = DateTimeOffset.UtcNow;
        var snapshot = Snapshot(now);
        await store.CreateAsync(snapshot, "cancel-race-2", null,
            TestContext.Current.CancellationToken);

        // A caller loads the execution while it is still running.
        var staleCopy = Assert.IsType<ExecutionSnapshot>(
            await store.GetAsync(snapshot.ExecutionId, TestContext.Current.CancellationToken));

        // The worker finishes it first.
        await store.UpdateAsync(
            staleCopy with
            {
                Status = ExecutionStatus.Succeeded,
                Outcome = Outcome(),
                UpdatedAt = now.AddSeconds(1)
            },
            TestContext.Current.CancellationToken);

        // The cancellation lands afterwards and must not resurrect the stale row.
        Assert.False(await store.TryRequestCancellationAsync(
            snapshot.ExecutionId,
            now.AddSeconds(2),
            TestContext.Current.CancellationToken));

        var persisted = Assert.IsType<ExecutionSnapshot>(
            await store.GetAsync(snapshot.ExecutionId, TestContext.Current.CancellationToken));
        Assert.Equal(ExecutionStatus.Succeeded, persisted.Status);
        Assert.NotNull(persisted.Outcome);
        Assert.Null(persisted.CancellationRequestedAt);
    }

    [Theory]
    [MemberData(nameof(StoreKinds))]
    public async Task RequestingCancellationTwiceKeepsTheFirstTimestamp(string storeKind)
    {
        await using var harness = await StoreHarness.CreateAsync(storeKind);
        var store = harness.Store;
        var now = DateTimeOffset.UtcNow;
        var snapshot = Snapshot(now);
        await store.CreateAsync(snapshot, "cancel-race-3", null,
            TestContext.Current.CancellationToken);

        var first = now.AddSeconds(1);
        Assert.True(await store.TryRequestCancellationAsync(
            snapshot.ExecutionId,
            first,
            TestContext.Current.CancellationToken));
        Assert.True(await store.TryRequestCancellationAsync(
            snapshot.ExecutionId,
            now.AddSeconds(5),
            TestContext.Current.CancellationToken));

        var persisted = Assert.IsType<ExecutionSnapshot>(
            await store.GetAsync(snapshot.ExecutionId, TestContext.Current.CancellationToken));
        Assert.Equal(
            first.ToUnixTimeMilliseconds(),
            persisted.CancellationRequestedAt?.ToUnixTimeMilliseconds());
    }

    private static TaskOutcome Outcome() => new(
        System.Text.Json.JsonSerializer.SerializeToElement(new { subject = "done" }),
        ResolutionLevel.SmallModel,
        Confidence: 1m,
        Evidence: [],
        Usage: new UsageSummary(InputTokens: 10, OutputTokens: 10, ModelCalls: 1),
        Artifacts: [],
        Validation: new ValidationSummary(true, 1m, [], []));

    private static ExecutionSnapshot Snapshot(DateTimeOffset now) => new(
        Guid.CreateVersion7(),
        "email.draft",
        ExecutionStatus.Accepted,
        now,
        now,
        TenantId: "tenant-a",
        ActorId: "test-runner");

    private sealed class StoreHarness : IAsyncDisposable
    {
        private readonly string? _databasePath;

        private StoreHarness(IExecutionStore store, string? databasePath)
        {
            Store = store;
            _databasePath = databasePath;
        }

        public IExecutionStore Store { get; }

        public static async Task<StoreHarness> CreateAsync(string storeKind)
        {
            if (storeKind == "memory")
            {
                return new StoreHarness(new InMemoryExecutionStore(), null);
            }

            var databasePath = Path.Combine(Path.GetTempPath(), $"iroute-cancel-{Guid.NewGuid():N}.db");
            var factory = new SqliteContextFactory(databasePath);
            await new SchemaMigrationManager(factory).UpgradeAsync(
                cancellationToken: TestContext.Current.CancellationToken);
            return new StoreHarness(new EfExecutionStore(factory, new NullExecutionFence()), databasePath);
        }

        public ValueTask DisposeAsync()
        {
            if (_databasePath is not null && File.Exists(_databasePath))
            {
                File.Delete(_databasePath);
            }

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
