using iRoute.Data;
using iRoute.Runtime.Composition;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace iRoute.Tests.Persistence;

public sealed class PersistenceStartupTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StartupInitializesStorageButOnlyMigratesWhenEnabled(bool autoInitialize)
    {
        var directory = Directory.CreateTempSubdirectory("iroute-startup-tests-");
        try
        {
            var database = Path.Combine(directory.FullName, "runtime.db");
            using var host = CreateHost($"Data Source={database};Pooling=False", autoInitialize);
            await host.StartAsync(TestContext.Current.CancellationToken);
            Assert.True(File.Exists(database));
            var factory = host.Services.GetRequiredService<IDbContextFactory<IRouteDbContext>>();
            await using var context = await factory.CreateDbContextAsync(TestContext.Current.CancellationToken);
            Assert.NotNull(context.Model.FindEntityType(typeof(ExecutionEntity)));
            var migrations = await context.Database.GetAppliedMigrationsAsync(TestContext.Current.CancellationToken);
            Assert.Equal(autoInitialize, migrations.Any());
            await host.StopAsync(TestContext.Current.CancellationToken);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task UnavailableStorageFailsStartupEvenWhenAutomaticMigrationsAreDisabled()
    {
        var directory = Directory.CreateTempSubdirectory("iroute-startup-tests-");
        try
        {
            using var host = CreateHost($"Data Source={Path.Combine(directory.FullName, "missing.db")};Mode=ReadOnly;Pooling=False", false);
            await Assert.ThrowsAsync<SqliteException>(() => host.StartAsync(TestContext.Current.CancellationToken));
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    private static IHost CreateHost(string connectionString, bool autoInitialize)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Storage:Provider"] = "Sqlite",
            ["Storage:AutoInitialize"] = autoInitialize.ToString(),
            ["ConnectionStrings:iRoute"] = connectionString,
            ["ModelGateway:Mode"] = "Deterministic"
        }).Build();
        return new HostBuilder().UseEnvironment("Development")
            .ConfigureServices(services =>
            {
                services.AddIRouteRuntime(configuration);
                services.AddIRoutePlatform(configuration);
            }).Build();
    }
}
