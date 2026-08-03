using System.Text.Json;
using iRoute.Contracts;
using iRoute.Core;
using iRoute.Infrastructure;
using iRoute.Runtime;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace iRoute.UnitTests;

/// <summary>
/// An idempotency key exists so a client can safely retry a submit it did not see the answer to.
/// The retry frequently races the original request, and reuse of a key with a different payload
/// must be reported rather than silently answered with an unrelated execution.
/// </summary>
public sealed class IdempotentSubmissionTests
{
    public static TheoryData<string> StoreKinds() => new() { "memory", "sqlite" };

    [Theory]
    [MemberData(nameof(StoreKinds))]
    public async Task ConcurrentSubmitsWithTheSameKeyResolveToOneExecution(string storeKind)
    {
        await using var harness = await StoreHarness.CreateAsync(storeKind);
        var store = harness.Store;
        var snapshot = Snapshot();

        // Exactly one insert wins; the loser reports the conflict rather than surfacing a
        // provider-specific unique-violation exception.
        var results = await Task.WhenAll(
            Capture(() => store.CreateAsync(
                snapshot,
                "shared-key",
                "fingerprint-a",
                TestContext.Current.CancellationToken)),
            Capture(() => store.CreateAsync(
                Snapshot(),
                "shared-key",
                "fingerprint-a",
                TestContext.Current.CancellationToken)));
        Assert.Equal(1, results.Count(outcome => outcome is null));
        Assert.Equal(1, results.Count(outcome => outcome is IdempotencyConflictException));

        var stored = await store.FindByIdempotencyKeyAsync(
            snapshot.TenantId,
            "shared-key",
            TestContext.Current.CancellationToken);
        Assert.NotNull(stored);
    }

    [Theory]
    [MemberData(nameof(StoreKinds))]
    public async Task ReusingAKeyWithADifferentPayloadIsDetectable(string storeKind)
    {
        await using var harness = await StoreHarness.CreateAsync(storeKind);
        var store = harness.Store;
        var original = Snapshot();
        await store.CreateAsync(
            original,
            "reused-key",
            "fingerprint-a",
            TestContext.Current.CancellationToken);

        var found = Assert.IsType<ExecutionSubmission>(await store.FindByIdempotencyKeyAsync(
            original.TenantId,
            "reused-key",
            TestContext.Current.CancellationToken));

        Assert.Equal(original.ExecutionId, found.Execution.ExecutionId);
        Assert.Equal("fingerprint-a", found.InputFingerprint);
        Assert.NotEqual("fingerprint-b", found.InputFingerprint);
    }

    [Theory]
    [MemberData(nameof(StoreKinds))]
    public async Task KeysAreScopedPerTenant(string storeKind)
    {
        await using var harness = await StoreHarness.CreateAsync(storeKind);
        var store = harness.Store;
        await store.CreateAsync(
            Snapshot("tenant-a"),
            "same-key",
            "fingerprint-a",
            TestContext.Current.CancellationToken);
        await store.CreateAsync(
            Snapshot("tenant-b"),
            "same-key",
            "fingerprint-b",
            TestContext.Current.CancellationToken);

        var a = await store.FindByIdempotencyKeyAsync(
            "tenant-a",
            "same-key",
            TestContext.Current.CancellationToken);
        var b = await store.FindByIdempotencyKeyAsync(
            "tenant-b",
            "same-key",
            TestContext.Current.CancellationToken);

        Assert.NotNull(a);
        Assert.NotNull(b);
        Assert.NotEqual(a.Execution.ExecutionId, b.Execution.ExecutionId);
    }

    [Fact]
    public async Task ReplayingAKeyWithADifferentPayloadIsRejectedBySubmit()
    {
        var store = new InMemoryExecutionStore();
        using var cancellations = new ExecutionCancellationRegistry();
        var orchestrator = ExecutionOrchestratorTests.CreateTestOrchestrator(store, cancellations);

        await orchestrator.SubmitAsync(
            Request("recipient-a"),
            TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<IdempotencyKeyReusedException>(() => orchestrator.SubmitAsync(
            Request("recipient-b"),
            TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ReplayingAKeyWithTheSamePayloadReturnsTheOriginalExecution()
    {
        var store = new InMemoryExecutionStore();
        using var cancellations = new ExecutionCancellationRegistry();
        var orchestrator = ExecutionOrchestratorTests.CreateTestOrchestrator(store, cancellations);

        var first = await orchestrator.SubmitAsync(
            Request("recipient-a"),
            TestContext.Current.CancellationToken);
        var replay = await orchestrator.SubmitAsync(
            Request("recipient-a"),
            TestContext.Current.CancellationToken);

        Assert.Equal(first.ExecutionId, replay.ExecutionId);
    }

    private static TaskRequest Request(string recipient) => new(
        "email.draft",
        JsonSerializer.SerializeToElement(new
        {
            recipient = new { name = recipient },
            projectName = "iRoute",
            objective = "Share the milestone.",
            tone = "professional"
        }),
        ProjectId: "project-1",
        IdempotencyKey: "replay-key",
        TenantId: "tenant-a",
        ActorId: "test-runner");

    /// <summary>
    /// Invokes inside the try so a store that throws synchronously is captured too.
    /// </summary>
    private static async Task<Exception?> Capture(Func<Task> work)
    {
        try
        {
            await Task.Run(work);
            return null;
        }
        catch (Exception exception)
        {
            return exception;
        }
    }

    private static ExecutionSnapshot Snapshot(string tenantId = "tenant-a")
    {
        var now = DateTimeOffset.UtcNow;
        return new ExecutionSnapshot(
            Guid.CreateVersion7(),
            "email.draft",
            ExecutionStatus.Accepted,
            now,
            now,
            TenantId: tenantId,
            ActorId: "test-runner");
    }

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

            var databasePath = Path.Combine(Path.GetTempPath(), $"iroute-idem-{Guid.NewGuid():N}.db");
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
