using System.Text.Json;
using iRoute.Contracts;
using iRoute.Core;
using iRoute.Infrastructure;
using iRoute.Worker;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace iRoute.UnitTests;

public sealed class LifecycleCleanupTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 1, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task CleanupNeverArchivesOrDeletesAnActiveDependency()
    {
        var policy = TestPolicy() with { MaxMemoryVersionsPerLineage = 1 };
        var artifacts = new InMemoryArtifactStore(policy);
        var memories = new InMemoryMemoryStore(policy);
        using var lifecycle = new InMemoryLifecycleStore(artifacts, memories);
        var first = await memories.UpsertAsync(
            CreateMemory("architecture", "SQLite", Now.AddDays(-10)),
            TestContext.Current.CancellationToken);
        var dependent = await artifacts.SaveAsync(
            CreateArtifact(
                "decision-summary",
                "summary-v1",
                Now.AddDays(-9),
                [new DependencyReference("memory", first.Record.MemoryId.ToString(), first.Record.ContentHash)]),
            TestContext.Current.CancellationToken);
        _ = await memories.UpsertAsync(
            CreateMemory("architecture", "PostgreSQL", Now.AddDays(-8)),
            TestContext.Current.CancellationToken);

        var sweep = await lifecycle.SweepAsync(policy, Now, TestContext.Current.CancellationToken);

        Assert.Equal(1, sweep.ProtectedActiveDependencies);
        Assert.Equal(0, sweep.ArchivedMemoryRecords);
        Assert.NotNull(await memories.GetAsync(
            "tenant-a",
            first.Record.MemoryId,
            TestContext.Current.CancellationToken));
        Assert.Equal(
            ArtifactLifecycleStatus.Active,
            (await artifacts.GetAsync(
                "tenant-a",
                dependent.ArtifactId,
                TestContext.Current.CancellationToken))!.LifecycleStatus);
    }

    [Fact]
    public async Task LifecycleWorkerRunsCleanupAsynchronously()
    {
        var store = new RecordingLifecycleStore();
        var policy = TestPolicy() with { SweepInterval = TimeSpan.FromMilliseconds(20) };
        var worker = new LifecycleWorker(
            store,
            policy,
            new FixedClock(Now),
            NullLogger<LifecycleWorker>.Instance);

        await worker.StartAsync(TestContext.Current.CancellationToken);
        await store.SweepObserved.Task.WaitAsync(
            TimeSpan.FromSeconds(2),
            TestContext.Current.CancellationToken);
        await worker.StopAsync(TestContext.Current.CancellationToken);

        Assert.True(store.SweepCount >= 1);
    }

    [Fact]
    public async Task QuotasBoundVersionedStorageThroughTwoPhaseArchival()
    {
        var policy = TestPolicy() with
        {
            MaxArtifactVersionsPerLineage = 2,
            MaxMemoryVersionsPerLineage = 2
        };
        var artifacts = new InMemoryArtifactStore(policy);
        var memories = new InMemoryMemoryStore(policy);
        using var lifecycle = new InMemoryLifecycleStore(artifacts, memories);
        for (var index = 0; index < 20; index++)
        {
            await artifacts.SaveAsync(
                CreateArtifact("bounded", $"artifact-{index}", Now.AddMinutes(index - 30)),
                TestContext.Current.CancellationToken);
        }

        for (var index = 0; index < 10; index++)
        {
            await memories.UpsertAsync(
                CreateMemory("bounded", $"memory-{index}", Now.AddMinutes(index - 30)),
                TestContext.Current.CancellationToken);
        }

        var archived = await lifecycle.SweepAsync(policy, Now, TestContext.Current.CancellationToken);
        var deleted = await lifecycle.SweepAsync(policy, Now.AddSeconds(1), TestContext.Current.CancellationToken);

        Assert.Equal(18, archived.ArchivedArtifacts);
        Assert.Equal(8, archived.ArchivedMemoryRecords);
        Assert.Equal(0, archived.DeletedArtifacts + archived.DeletedMemoryRecords);
        Assert.Equal(18, deleted.DeletedArtifacts);
        Assert.Equal(8, deleted.DeletedMemoryRecords);
        Assert.Equal(2, deleted.After.ArtifactCount);
        Assert.Equal(2, deleted.After.MemoryCount);
        Assert.Equal(0, deleted.After.ArchiveCount);
        Assert.Equal(0, deleted.After.DanglingDependencyEdgeCount);
    }

    [Fact]
    public async Task TtlExpirationInvalidatesDerivedStateBeforeCleanup()
    {
        var policy = TestPolicy() with { ArchiveAfterInactive = TimeSpan.FromDays(30) };
        var artifacts = new InMemoryArtifactStore(policy);
        var memories = new InMemoryMemoryStore(policy);
        using var lifecycle = new InMemoryLifecycleStore(artifacts, memories);
        var source = await memories.UpsertAsync(
            CreateMemory("temporary-fact", "expires", Now.AddDays(-2), Now.AddMinutes(-1)),
            TestContext.Current.CancellationToken);
        var dependent = await artifacts.SaveAsync(
            CreateArtifact(
                "derived",
                "derived-v1",
                Now.AddDays(-1),
                [new DependencyReference("memory", source.Record.MemoryId.ToString(), source.Record.ContentHash)]),
            TestContext.Current.CancellationToken);

        var result = await lifecycle.SweepAsync(policy, Now, TestContext.Current.CancellationToken);

        Assert.Equal(1, result.ExpiredMemoryRecords);
        Assert.Equal(1, result.InvalidatedArtifacts);
        Assert.Null(await memories.GetActiveAsync(
            new MemoryLookup("tenant-a", "project-1", MemoryKind.Decision, "temporary-fact", Now),
            TestContext.Current.CancellationToken));
        Assert.Equal(
            ArtifactLifecycleStatus.Invalidated,
            (await artifacts.GetAsync(
                "tenant-a",
                dependent.ArtifactId,
                TestContext.Current.CancellationToken))!.LifecycleStatus);
    }

    [Fact]
    public async Task ExplicitDeletionPropagatesAndRemovesIndexesAndArchive()
    {
        var policy = TestPolicy();
        var artifacts = new InMemoryArtifactStore(policy);
        var memories = new InMemoryMemoryStore(policy);
        using var lifecycle = new InMemoryLifecycleStore(artifacts, memories);
        var memory = await memories.UpsertAsync(
            CreateMemory("source", "value", Now.AddDays(-2)),
            TestContext.Current.CancellationToken);
        var first = await artifacts.SaveAsync(
            CreateArtifact(
                "first",
                "first-v1",
                Now.AddDays(-1),
                [new DependencyReference("memory", memory.Record.MemoryId.ToString(), memory.Record.ContentHash)]),
            TestContext.Current.CancellationToken);
        var second = await artifacts.SaveAsync(
            CreateArtifact(
                "second",
                "second-v1",
                Now,
                [new DependencyReference("artifact", first.ArtifactId.ToString(), first.ContentHash)]),
            TestContext.Current.CancellationToken);

        var deletion = await lifecycle.DeleteAsync(
            new LifecycleDeletionRequest(
                "tenant-a",
                LifecycleResourceKind.Memory,
                memory.Record.MemoryId,
                "The source was deleted by its owner.",
                Now),
            TestContext.Current.CancellationToken);
        var snapshot = await lifecycle.InspectAsync(TestContext.Current.CancellationToken);

        Assert.True(deletion.Deleted);
        Assert.Equal(2, deletion.InvalidatedArtifacts);
        Assert.True(deletion.RemovedDependencyEdges >= 1);
        Assert.Null(await memories.GetAsync(
            "tenant-a",
            memory.Record.MemoryId,
            TestContext.Current.CancellationToken));
        Assert.Equal(
            ArtifactLifecycleStatus.Invalidated,
            (await artifacts.GetAsync("tenant-a", first.ArtifactId, TestContext.Current.CancellationToken))!.LifecycleStatus);
        Assert.Equal(
            ArtifactLifecycleStatus.Invalidated,
            (await artifacts.GetAsync("tenant-a", second.ArtifactId, TestContext.Current.CancellationToken))!.LifecycleStatus);
        Assert.Equal(0, snapshot.DanglingDependencyEdgeCount);
    }

    [Fact]
    public async Task SqlitePersistsArchiveThenDeletesColdVersionsAcrossRestart()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"iroute-lifecycle-{Guid.NewGuid():N}.db");
        try
        {
            var factory = new SqliteContextFactory(databasePath);
            await using (var context = factory.CreateDbContext())
            {
                await context.Database.MigrateAsync(TestContext.Current.CancellationToken);
                Assert.Contains(
                    iRoute.Infrastructure.Migrations.LifecycleCleanup.MigrationId,
                    await context.Database.GetAppliedMigrationsAsync(TestContext.Current.CancellationToken));
            }

            var policy = TestPolicy() with
            {
                MaxArtifactVersionsPerLineage = 2,
                ArchiveRetention = TimeSpan.FromDays(30)
            };
            var artifacts = new EfArtifactStore(factory, policy);
            var created = new List<ArtifactRecord>();
            for (var index = 0; index < 6; index++)
            {
                created.Add(await artifacts.SaveAsync(
                    CreateArtifact("sqlite", $"content-{index}", Now.AddMinutes(index - 20)),
                    TestContext.Current.CancellationToken));
            }

            var firstSweep = await new EfLifecycleStore(factory).SweepAsync(
                policy,
                Now,
                TestContext.Current.CancellationToken);
            var secondSweep = await new EfLifecycleStore(factory).SweepAsync(
                policy,
                Now.AddSeconds(1),
                TestContext.Current.CancellationToken);
            var restarted = new EfLifecycleStore(factory);
            var snapshot = await restarted.InspectAsync(TestContext.Current.CancellationToken);

            Assert.Equal(4, firstSweep.ArchivedArtifacts);
            Assert.Equal(0, firstSweep.DeletedArtifacts);
            Assert.Equal(4, secondSweep.DeletedArtifacts);
            Assert.Equal(2, snapshot.ArtifactCount);
            Assert.Equal(4, snapshot.ArchiveCount);
            Assert.Equal(0, snapshot.DanglingDependencyEdgeCount);
            Assert.Null(await new EfArtifactStore(factory, policy).GetAsync(
                "tenant-a",
                created[0].ArtifactId,
                TestContext.Current.CancellationToken));
        }
        finally
        {
            File.Delete(databasePath);
        }
    }

    [Fact]
    public async Task SqliteDeletionInvalidatesDerivedStateAndRemovesDependencyIndexRows()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"iroute-delete-{Guid.NewGuid():N}.db");
        try
        {
            var factory = new SqliteContextFactory(databasePath);
            await using (var context = factory.CreateDbContext())
            {
                await context.Database.MigrateAsync(TestContext.Current.CancellationToken);
            }

            var policy = TestPolicy();
            var memories = new EfMemoryStore(factory, policy);
            var artifacts = new EfArtifactStore(factory, policy);
            var memory = await memories.UpsertAsync(
                CreateMemory("source", "durable", Now.AddDays(-2)),
                TestContext.Current.CancellationToken);
            var artifact = await artifacts.SaveAsync(
                CreateArtifact(
                    "derived",
                    "durable-derived",
                    Now.AddDays(-1),
                    [new DependencyReference(
                        "memory",
                        memory.Record.MemoryId.ToString(),
                        memory.Record.ContentHash)]),
                TestContext.Current.CancellationToken);
            var lifecycle = new EfLifecycleStore(factory);

            var result = await lifecycle.DeleteAsync(
                new LifecycleDeletionRequest(
                    "tenant-a",
                    LifecycleResourceKind.Memory,
                    memory.Record.MemoryId,
                    "Owner deletion request.",
                    Now),
                TestContext.Current.CancellationToken);
            var snapshot = await new EfLifecycleStore(factory).InspectAsync(
                TestContext.Current.CancellationToken);

            Assert.True(result.Deleted);
            Assert.Equal(1, result.InvalidatedArtifacts);
            Assert.True(result.RemovedDependencyEdges >= 1);
            Assert.Null(await new EfMemoryStore(factory, policy).GetAsync(
                "tenant-a",
                memory.Record.MemoryId,
                TestContext.Current.CancellationToken));
            Assert.Equal(
                ArtifactLifecycleStatus.Invalidated,
                (await new EfArtifactStore(factory, policy).GetAsync(
                    "tenant-a",
                    artifact.ArtifactId,
                    TestContext.Current.CancellationToken))!.LifecycleStatus);
            Assert.Equal(0, snapshot.DependencyEdgeCount);
            Assert.Equal(0, snapshot.DanglingDependencyEdgeCount);
        }
        finally
        {
            File.Delete(databasePath);
        }
    }

    private static LifecyclePolicy TestPolicy() => new()
    {
        SweepInterval = TimeSpan.FromSeconds(1),
        DefaultArtifactTimeToLive = TimeSpan.FromDays(365),
        DefaultMemoryTimeToLive = TimeSpan.FromDays(365),
        ArchiveAfterInactive = TimeSpan.FromDays(365),
        DeleteAfterArchive = TimeSpan.Zero,
        ArchiveRetention = TimeSpan.Zero,
        MaxArtifactVersionsPerLineage = 5,
        MaxMemoryVersionsPerLineage = 5,
        MaxArtifactsPerTenant = 100,
        MaxMemoryRecordsPerTenant = 100,
        MaxArchivesPerTenant = 100,
        BatchSize = 100
    };

    private static ArtifactRecord CreateArtifact(
        string logicalKey,
        string content,
        DateTimeOffset createdAt,
        IReadOnlyList<DependencyReference>? dependencies = null,
        DateTimeOffset? expiresAt = null)
    {
        var value = JsonSerializer.SerializeToElement(new { content });
        return new ArtifactRecord(
            Guid.CreateVersion7(),
            "tenant-a",
            "project-1",
            "email.draft",
            1,
            "email.draft",
            1,
            $"input-{content}",
            $"hash-{content}",
            value,
            [],
            createdAt,
            expiresAt ?? Now.AddDays(100),
            true,
            logicalKey,
            Dependencies: dependencies ?? []);
    }

    private static MemoryRecord CreateMemory(
        string key,
        string value,
        DateTimeOffset createdAt,
        DateTimeOffset? expiresAt = null) => new(
            Guid.CreateVersion7(),
            "tenant-a",
            "project-1",
            MemoryKind.Decision,
            key,
            1,
            JsonSerializer.SerializeToElement(value),
            $"hash-{value}",
            MemoryLifecycleStatus.Active,
            [],
            [],
            createdAt,
            expiresAt ?? Now.AddDays(100));

    private sealed class SqliteContextFactory(string databasePath) : IDbContextFactory<IRouteDbContext>
    {
        private readonly DbContextOptions<IRouteDbContext> _options =
            new DbContextOptionsBuilder<IRouteDbContext>()
                .UseSqlite($"Data Source={databasePath}")
                .Options;

        public IRouteDbContext CreateDbContext() => new(_options);
    }

    private sealed class FixedClock(DateTimeOffset now) : IClock
    {
        public DateTimeOffset UtcNow => now;
    }

    private sealed class RecordingLifecycleStore : ILifecycleStore
    {
        public TaskCompletionSource SweepObserved { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        public int SweepCount { get; private set; }

        public Task<LifecycleSweepResult> SweepAsync(
            LifecyclePolicy policy,
            DateTimeOffset now,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            SweepCount++;
            SweepObserved.TrySetResult();
            var snapshot = new LifecycleStorageSnapshot(0, 0, 0, 0, 0);
            return Task.FromResult(new LifecycleSweepResult(
                now,
                now,
                0,
                0,
                0,
                0,
                0,
                0,
                0,
                0,
                0,
                0,
                snapshot,
                snapshot));
        }

        public Task<LifecycleDeletionResult> DeleteAsync(
            LifecycleDeletionRequest request,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<LifecycleStorageSnapshot> InspectAsync(CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }
}
