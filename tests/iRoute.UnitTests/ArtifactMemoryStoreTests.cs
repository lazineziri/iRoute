using System.Text.Json;
using iRoute.Contracts;
using iRoute.Core;
using iRoute.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace iRoute.UnitTests;

public sealed class ArtifactMemoryStoreTests
{
    [Fact]
    public async Task ArtifactLineageIsVersionedDeduplicatedAndTenantScoped()
    {
        var store = new InMemoryArtifactStore();
        var first = await store.SaveAsync(
            CreateArtifact("tenant-a", "project-1", "draft", "input-1", "content-1"),
            TestContext.Current.CancellationToken);
        var duplicate = await store.SaveAsync(
            CreateArtifact("tenant-a", "project-1", "draft", "input-1", "content-1"),
            TestContext.Current.CancellationToken);
        var second = await store.SaveAsync(
            CreateArtifact("tenant-a", "project-1", "draft", "input-2", "content-2"),
            TestContext.Current.CancellationToken);

        Assert.Equal(first.ArtifactId, duplicate.ArtifactId);
        Assert.Equal(1, first.Version);
        Assert.Equal(2, second.Version);
        Assert.Equal(first.ArtifactId, second.SupersedesArtifactId);
        var superseded = await store.GetAsync(
            "tenant-a",
            first.ArtifactId,
            TestContext.Current.CancellationToken);
        Assert.NotNull(superseded);
        Assert.Equal(ArtifactLifecycleStatus.Superseded, superseded.LifecycleStatus);
        Assert.Equal(second.ArtifactId, superseded.SupersededByArtifactId);
        Assert.False(superseded.IsActive);
        Assert.Null(await store.GetAsync(
            "tenant-b",
            first.ArtifactId,
            TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ArtifactInvalidationCascadesAlongDependencyEdgesWithinTenant()
    {
        var store = new InMemoryArtifactStore();
        var memoryId = Guid.CreateVersion7();
        var source = await store.SaveAsync(
            CreateArtifact(
                "tenant-a",
                "project-1",
                "source",
                "input-source",
                "content-source",
                [new DependencyReference("memory", memoryId.ToString(), "memory-v1")]),
            TestContext.Current.CancellationToken);
        var derived = await store.SaveAsync(
            CreateArtifact(
                "tenant-a",
                "project-1",
                "derived",
                "input-derived",
                "content-derived",
                [new DependencyReference("artifact", source.ArtifactId.ToString(), source.ContentHash)]),
            TestContext.Current.CancellationToken);
        var otherTenant = await store.SaveAsync(
            CreateArtifact(
                "tenant-b",
                "project-1",
                "source",
                "input-source",
                "content-source",
                [new DependencyReference("memory", memoryId.ToString(), "memory-v1")]),
            TestContext.Current.CancellationToken);

        var result = await store.InvalidateByDependencyAsync(
            new DependencyChange(
                "tenant-a",
                "memory",
                memoryId.ToString(),
                null,
                true,
                "The decision was superseded.",
                DateTimeOffset.UtcNow),
            TestContext.Current.CancellationToken);

        Assert.Equal(new[] { source.ArtifactId, derived.ArtifactId }.Order(), result.ArtifactIds.Order());
        Assert.Equal(
            ArtifactLifecycleStatus.Invalidated,
            (await store.GetAsync("tenant-a", derived.ArtifactId, TestContext.Current.CancellationToken))!.LifecycleStatus);
        Assert.Equal(
            ArtifactLifecycleStatus.Active,
            (await store.GetAsync("tenant-b", otherTenant.ArtifactId, TestContext.Current.CancellationToken))!.LifecycleStatus);
    }

    [Fact]
    public async Task MemoryLineageIsVersionedDeduplicatedAndTenantScoped()
    {
        var store = new InMemoryMemoryStore();
        var first = await store.UpsertAsync(
            CreateMemory("tenant-a", "project-1", "architecture", "SQLite"),
            TestContext.Current.CancellationToken);
        var duplicate = await store.UpsertAsync(
            CreateMemory("tenant-a", "project-1", "architecture", "SQLite"),
            TestContext.Current.CancellationToken);
        var second = await store.UpsertAsync(
            CreateMemory("tenant-a", "project-1", "architecture", "PostgreSQL"),
            TestContext.Current.CancellationToken);

        Assert.True(first.Created);
        Assert.False(duplicate.Created);
        Assert.Equal(first.Record.MemoryId, duplicate.Record.MemoryId);
        Assert.Equal(2, second.Record.Version);
        Assert.Equal(first.Record.MemoryId, second.Record.SupersedesMemoryId);
        Assert.Equal(
            MemoryLifecycleStatus.Superseded,
            (await store.GetAsync(
                "tenant-a",
                first.Record.MemoryId,
                TestContext.Current.CancellationToken))!.LifecycleStatus);
        Assert.Equal(
            second.Record.MemoryId,
            (await store.GetActiveAsync(
                new MemoryLookup(
                    "tenant-a",
                    "project-1",
                    MemoryKind.Decision,
                    "architecture",
                    DateTimeOffset.UtcNow),
                TestContext.Current.CancellationToken))!.MemoryId);
        Assert.Null(await store.GetAsync(
            "tenant-b",
            first.Record.MemoryId,
            TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task SqlitePersistsMemoryArtifactLineageAndDependencyInvalidationAcrossRestart()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"iroute-state-{Guid.NewGuid():N}.db");
        try
        {
            var factory = new SqliteContextFactory(databasePath);
            await using (var context = factory.CreateDbContext())
            {
                await context.Database.MigrateAsync(TestContext.Current.CancellationToken);
                Assert.Contains(
                    iRoute.Infrastructure.Migrations.ArtifactMemoryStore.MigrationId,
                    await context.Database.GetAppliedMigrationsAsync(TestContext.Current.CancellationToken));
            }

            var memories = new EfMemoryStore(factory);
            var artifacts = new EfArtifactStore(factory);
            var memory = await memories.UpsertAsync(
                CreateMemory("tenant-a", "project-1", "architecture", "SQLite"),
                TestContext.Current.CancellationToken);
            var artifact = await artifacts.SaveAsync(
                CreateArtifact(
                    "tenant-a",
                    "project-1",
                    "draft",
                    "input-1",
                    "content-1",
                    [new DependencyReference(
                        "memory",
                        memory.Record.MemoryId.ToString(),
                        memory.Record.ContentHash)]),
                TestContext.Current.CancellationToken);

            var restartedMemories = new EfMemoryStore(factory);
            var restartedArtifacts = new EfArtifactStore(factory);
            var persistedMemory = await restartedMemories.GetAsync(
                "tenant-a",
                memory.Record.MemoryId,
                TestContext.Current.CancellationToken);
            var persistedArtifact = await restartedArtifacts.GetAsync(
                "tenant-a",
                artifact.ArtifactId,
                TestContext.Current.CancellationToken);
            Assert.NotNull(persistedMemory);
            Assert.NotNull(persistedArtifact);
            Assert.Contains(
                persistedArtifact.EffectiveDependencies,
                dependency => dependency.Reference == memory.Record.MemoryId.ToString());
            Assert.Null(await restartedArtifacts.GetAsync(
                "tenant-b",
                artifact.ArtifactId,
                TestContext.Current.CancellationToken));

            await restartedArtifacts.InvalidateByDependencyAsync(
                new DependencyChange(
                    "tenant-a",
                    "memory",
                    memory.Record.MemoryId.ToString(),
                    null,
                    true,
                    "The decision was superseded.",
                    DateTimeOffset.UtcNow),
                TestContext.Current.CancellationToken);
            Assert.Equal(
                ArtifactLifecycleStatus.Invalidated,
                (await new EfArtifactStore(factory).GetAsync(
                    "tenant-a",
                    artifact.ArtifactId,
                    TestContext.Current.CancellationToken))!.LifecycleStatus);
        }
        finally
        {
            File.Delete(databasePath);
        }
    }

    [Fact]
    public async Task SqliteUpgradeDeterministicallyBackfillsExistingArtifactLineage()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"iroute-upgrade-{Guid.NewGuid():N}.db");
        try
        {
            var factory = new SqliteContextFactory(databasePath);
            var firstId = Guid.CreateVersion7();
            var secondId = Guid.CreateVersion7();
            await using (var context = factory.CreateDbContext())
            {
                var migrator = context.GetService<IMigrator>();
                await migrator.MigrateAsync(
                    iRoute.Infrastructure.Migrations.PolicyApprovals.MigrationId,
                    TestContext.Current.CancellationToken);
                await context.Database.ExecuteSqlInterpolatedAsync(
                    $$"""
                    INSERT INTO "Artifacts" (
                        "ArtifactId", "TenantId", "ProjectId", "TaskType", "TaskDefinitionVersion",
                        "ArtifactType", "Version", "InputHash", "ContentHash", "ContentJson",
                        "EvidenceJson", "CreatedAtUnixMilliseconds", "ExpiresAtUnixMilliseconds", "IsActive")
                    VALUES
                        ({{firstId}}, 'tenant-a', 'project-1', 'email.draft', 1,
                         'email.draft', 1, 'input-1', 'content-1', '{}', '[]', 1, NULL, 1),
                        ({{secondId}}, 'tenant-a', 'project-1', 'email.draft', 1,
                         'email.draft', 2, 'input-2', 'content-2', '{}', '[]', 2, NULL, 1);
                    """,
                    TestContext.Current.CancellationToken);
                await migrator.MigrateAsync(
                    iRoute.Infrastructure.Migrations.ArtifactMemoryStore.MigrationId,
                    TestContext.Current.CancellationToken);
            }

            var store = new EfArtifactStore(factory);
            var first = await store.GetAsync(
                "tenant-a",
                firstId,
                TestContext.Current.CancellationToken);
            var second = await store.GetAsync(
                "tenant-a",
                secondId,
                TestContext.Current.CancellationToken);
            Assert.NotNull(first);
            Assert.NotNull(second);
            Assert.Equal("email.draft", first.LogicalKey);
            Assert.Equal(ArtifactLifecycleStatus.Superseded, first.LifecycleStatus);
            Assert.False(first.IsActive);
            Assert.Equal(secondId, first.SupersededByArtifactId);
            Assert.Equal(ArtifactLifecycleStatus.Active, second.LifecycleStatus);
            Assert.True(second.IsActive);
            Assert.Equal(firstId, second.SupersedesArtifactId);
        }
        finally
        {
            File.Delete(databasePath);
        }
    }

    private static ArtifactRecord CreateArtifact(
        string tenantId,
        string projectId,
        string logicalKey,
        string input,
        string content,
        IReadOnlyList<DependencyReference>? dependencies = null)
    {
        var value = JsonSerializer.SerializeToElement(new { content });
        return new ArtifactRecord(
            Guid.CreateVersion7(),
            tenantId,
            projectId,
            "email.draft",
            1,
            "email.draft",
            1,
            input,
            $"hash-{content}",
            value,
            [],
            DateTimeOffset.UtcNow,
            null,
            true,
            logicalKey,
            Dependencies: dependencies ?? []);
    }

    private static MemoryRecord CreateMemory(
        string tenantId,
        string projectId,
        string key,
        string value)
    {
        var content = JsonSerializer.SerializeToElement(value);
        return new MemoryRecord(
            Guid.CreateVersion7(),
            tenantId,
            projectId,
            MemoryKind.Decision,
            key,
            1,
            content,
            $"hash-{value}",
            MemoryLifecycleStatus.Active,
            [],
            [],
            DateTimeOffset.UtcNow);
    }

    private sealed class SqliteContextFactory(string databasePath) : IDbContextFactory<IRouteDbContext>
    {
        private readonly DbContextOptions<IRouteDbContext> _options =
            new DbContextOptionsBuilder<IRouteDbContext>()
                .UseSqlite($"Data Source={databasePath}")
                .Options;

        public IRouteDbContext CreateDbContext() => new(_options);
    }
}
