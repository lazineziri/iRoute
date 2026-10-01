using System.Text.Json;
using iRoute.Common;
using iRoute.Data;
using iRoute.Data.Migrations;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace iRoute.Tests.Postgres;

public sealed class PostgresConcurrencyTests
{
    public static bool Enabled => PostgresFixture.Enabled;
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private static readonly DateTimeOffset Now = DateTimeOffset.FromUnixTimeSeconds(1800000005);

    [Fact(Skip = "Requires IROUTE_POSTGRES_TEST_CONNECTION to a disposable database.", SkipUnless = nameof(Enabled))]
    public async Task CompetingWorkersClaimEachExecutionOnlyOnce()
    {
        await using var fixture = await PostgresFixture.CreateAsync();
        var id = await EnqueueAsync(fixture, "tenant-a");
        var work = fixture.Services.GetRequiredService<IExecutionWorkStore>();
        var claims = await Task.WhenAll(Enumerable.Range(0, 12).Select(index => work.TryClaimAsync("worker-" + index,
            Now, TimeSpan.FromSeconds(10), Token)));
        Assert.Single(claims, claim => claim is not null);
        Assert.Equal(id, claims.Single(claim => claim is not null)!.ExecutionId);
    }

    [Fact(Skip = "Requires IROUTE_POSTGRES_TEST_CONNECTION to a disposable database.", SkipUnless = nameof(Enabled))]
    public async Task CrashTakeoverFencesTheOldWorkerAndPreservesCancellation()
    {
        await using var fixture = await PostgresFixture.CreateAsync();
        var id = await EnqueueAsync(fixture, "tenant-a");
        var work = fixture.Services.GetRequiredService<IExecutionWorkStore>();
        var old = (await work.TryClaimAsync("old", Now, TimeSpan.FromSeconds(10), Token))!;
        var executions = fixture.Services.GetRequiredService<IExecutionStore>();
        await executions.TryRequestCancellationAsync(id, Now.AddSeconds(1), Token);
        var takeover = (await work.TryClaimAsync("new", Now.AddSeconds(11), TimeSpan.FromSeconds(10), Token))!;
        Assert.NotEqual(old.LeaseToken, takeover.LeaseToken);
        Assert.False(await work.CompleteAsync(old, Now.AddSeconds(12), Token));
        Assert.False((await work.RenewAsync(old, Now.AddSeconds(12), TimeSpan.FromSeconds(10), Token)).Renewed);
        using (fixture.Services.GetRequiredService<IExecutionFence>().Hold(old.LeaseToken))
            await Assert.ThrowsAsync<LeaseFencedException>(() => executions.AppendEventAsync(id, "stale-write",
                Now.AddSeconds(12), JsonSerializer.SerializeToElement(new { stale = true }), Token));
        var heartbeat = await work.RenewAsync(takeover, Now.AddSeconds(12), TimeSpan.FromSeconds(10), Token);
        Assert.True(heartbeat.Renewed);
        Assert.True(heartbeat.CancellationRequested);
        Assert.True(await work.CompleteAsync(takeover, Now.AddSeconds(13), Token));
    }

    [Fact(Skip = "Requires IROUTE_POSTGRES_TEST_CONNECTION to a disposable database.", SkipUnless = nameof(Enabled))]
    public async Task TenantFairnessPreventsABacklogFromStarvingAnotherTenant()
    {
        await using var fixture = await PostgresFixture.CreateAsync(configure: services => services.AddSingleton(
            new TenantQuotaOptions { Enabled = true, Default = new TenantQuotaPolicy { MaxConcurrentExecutions = 1 } }));
        var first = await EnqueueAsync(fixture, "a-busy");
        for (var index = 0; index < 20; index++) await EnqueueAsync(fixture, "a-busy");
        var other = await EnqueueAsync(fixture, "b-small");
        var work = fixture.Services.GetRequiredService<IExecutionWorkStore>();
        var a = (await work.TryClaimAsync("a", Now, TimeSpan.FromMinutes(1), Token))!;
        var b = (await work.TryClaimAsync("b", Now, TimeSpan.FromMinutes(1), Token))!;
        Assert.Equal(first, a.ExecutionId);
        Assert.Equal(other, b.ExecutionId);
        Assert.Null(await work.TryClaimAsync("blocked", Now, TimeSpan.FromMinutes(1), Token));
        await work.CompleteAsync(a, Now, Token);
        Assert.NotNull(await work.TryClaimAsync("after-release", Now, TimeSpan.FromMinutes(1), Token));
    }

    [Fact(Skip = "Requires IROUTE_POSTGRES_TEST_CONNECTION to a disposable database.", SkipUnless = nameof(Enabled))]
    public async Task ConcurrentQuotaReservationsStayAtomicAcrossIndependentConnections()
    {
        await using var fixture = await PostgresFixture.CreateAsync();
        var quotas = fixture.Services.GetRequiredService<ITenantQuotaStore>();
        var policy = new TenantQuotaPolicy { MaxConcurrentModelCalls = 3, MaxTokensPerWindow = 300 };
        var permits = await Task.WhenAll(Enumerable.Range(0, 16).Select(_ => quotas.TryReserveAsync(
            new TenantQuotaReservation(Guid.CreateVersion7(), "tenant", 100, null, Now, Now.AddSeconds(30), 60), policy, Token)));
        Assert.Equal(3, permits.Count(item => item.Accepted));
        var snapshot = await quotas.InspectAsync("tenant", Now, 60, Token);
        Assert.Equal(3, snapshot.ActiveModelCalls);
        Assert.Equal(300, snapshot.ChargedTokens);
    }

    [Fact(Skip = "Requires IROUTE_POSTGRES_TEST_CONNECTION to a disposable database.", SkipUnless = nameof(Enabled))]
    public async Task ConcurrentMemoryVersionsCannotOverwriteTheNewestProducerVersion()
    {
        await using var fixture = await PostgresFixture.CreateAsync();
        var memories = fixture.Services.GetRequiredService<IMemoryStore>();
        await Task.WhenAll(Enumerable.Range(1, 12).Select(version => memories.UpsertAsync(new MemoryRecord(Guid.CreateVersion7(),
            "tenant", "project", MemoryKind.Fact, "meeting", 1, JsonSerializer.SerializeToElement(new { version }),
            version.ToString(System.Globalization.CultureInfo.InvariantCulture), MemoryLifecycleStatus.Active, [], [], Now), Token)));
        var active = await memories.GetActiveAsync(new MemoryLookup("tenant", "project", MemoryKind.Fact, "meeting", Now), Token);
        Assert.Equal(12, active!.Value.GetProperty("version").GetInt32());
        Assert.Single(await memories.ListActiveAsync(new ActiveMemoryQuery("tenant", "project", Now), Token));
    }

    [Fact(Skip = "Requires IROUTE_POSTGRES_TEST_CONNECTION to a disposable database.", SkipUnless = nameof(Enabled))]
    public async Task UpgradePreservesLegacyDataAndLeavesUnknownConfidenceUnknown()
    {
        await using var fixture = await PostgresFixture.CreateAsync(migrationTarget: IdempotencyFingerprint.MigrationId);
        var contexts = fixture.Services.GetRequiredService<IDbContextFactory<IRouteDbContext>>();
        await using (var context = await contexts.CreateDbContextAsync(Token))
            await context.Database.ExecuteSqlRawAsync("INSERT INTO \"Artifacts\" (\"ArtifactId\", \"TenantId\", \"ProjectId\", \"TaskType\", \"TaskDefinitionVersion\", \"ArtifactType\", \"Version\", \"InputHash\", \"ContentHash\", \"ContentJson\", \"EvidenceJson\", \"CreatedAtUnixMilliseconds\", \"IsActive\", \"LogicalKey\", \"LifecycleStatus\") VALUES ('00000000-0000-0000-0000-000000000001', 'tenant', '', 'email.draft', 1, 'email-draft', 1, 'legacy', 'legacy', {0}, '[]', 1, true, 'email.draft', 'Active')", ["{}"], Token);
        var migrations = fixture.Services.GetRequiredService<SchemaMigrationManager>();
        var upgraded = await migrations.UpgradeAsync(cancellationToken: Token);
        Assert.Empty(upgraded.PendingMigrations);
        var artifact = await fixture.Services.GetRequiredService<IArtifactStore>().GetAsync("tenant",
            Guid.Parse("00000000-0000-0000-0000-000000000001"), Token);
        Assert.NotNull(artifact);
        Assert.Null(artifact.Confidence);
        await migrations.RollbackAsync(IdempotencyFingerprint.MigrationId, true, Token);
        await migrations.UpgradeAsync(cancellationToken: Token);
        Assert.NotNull(await fixture.Services.GetRequiredService<IArtifactStore>().GetAsync("tenant", artifact.ArtifactId, Token));
    }

    private static async Task<Guid> EnqueueAsync(PostgresFixture fixture, string tenant)
    {
        var id = Guid.CreateVersion7();
        await fixture.Services.GetRequiredService<IExecutionStore>().CreateAsync(new ExecutionSnapshot(id, "email.draft",
            ExecutionStatus.Accepted, Now, Now, TenantId: tenant, ActorId: "tester"), null, null, Token);
        await fixture.Services.GetRequiredService<IExecutionWorkStore>().EnqueueAsync(id, ExecutionStatus.Accepted, Now, Token);
        return id;
    }
}
