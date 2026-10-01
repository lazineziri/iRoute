using iRoute.Common;
using iRoute.Data;
using iRoute.Data.Migrations;
using iRoute.Tests.Executions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace iRoute.Tests.Quotas;

public sealed class QuotaMigrationTests
{
    [Fact]
    public async Task SQLiteUpgradeAndRollbackPreserveLegacyArtifacts()
    {
        var token = TestContext.Current.CancellationToken;
        await using var fixture = await ExecutionFixture.CreateAsync(migrationTarget: IdempotencyFingerprint.MigrationId);
        await using (var context = await fixture.Services.GetRequiredService<IDbContextFactory<IRouteDbContext>>().CreateDbContextAsync(token))
            await context.Database.ExecuteSqlRawAsync("INSERT INTO \"Artifacts\" (\"ArtifactId\", \"TenantId\", \"ProjectId\", \"TaskType\", \"TaskDefinitionVersion\", \"ArtifactType\", \"Version\", \"InputHash\", \"ContentHash\", \"ContentJson\", \"EvidenceJson\", \"CreatedAtUnixMilliseconds\", \"IsActive\", \"LogicalKey\", \"LifecycleStatus\") VALUES ('00000000-0000-0000-0000-000000000001', 'tenant', '', 'email.draft', 1, 'email-draft', 1, 'legacy', 'legacy', {0}, '[]', 1, true, 'email.draft', 'Active')", ["{}"], token);
        var migrations = fixture.Services.GetRequiredService<SchemaMigrationManager>();
        await migrations.UpgradeAsync(cancellationToken: token);
        var id = Guid.Parse("00000000-0000-0000-0000-000000000001");
        var artifacts = fixture.Services.GetRequiredService<IArtifactStore>();
        Assert.Null((await artifacts.GetAsync("tenant", id, token))!.Confidence);
        await migrations.RollbackAsync(IdempotencyFingerprint.MigrationId, true, token);
        await migrations.UpgradeAsync(cancellationToken: token);
        Assert.NotNull(await artifacts.GetAsync("tenant", id, token));
    }
}
