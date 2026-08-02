using iRoute.Infrastructure;
using Microsoft.Data.Sqlite;

namespace iRoute.UnitTests;

public sealed class SqliteStoragePathTests : IDisposable
{
    private readonly string sharedBase =
        Path.Combine(Path.GetTempPath(), $"iroute-test-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(sharedBase)) Directory.Delete(sharedBase, recursive: true);
    }

    private static string DataSourceOf(string connectionString) =>
        new SqliteConnectionStringBuilder(connectionString).DataSource;

    [Fact]
    public void ApiAndWorkerResolveTheSameFileFromDifferentWorkingDirectories()
    {
        // The documented quickstart starts each host with `dotnet run --project`, which sets a
        // different working directory per project. A relative Data Source would otherwise resolve
        // to a separate database per host and queued executions would never be processed.
        var api = SqliteStoragePath.Resolve("Data Source=iroute.db", sharedBase);
        var worker = SqliteStoragePath.Resolve("Data Source=iroute.db", sharedBase);

        Assert.Equal(api, worker);
        Assert.Equal(Path.Combine(sharedBase, "iroute.db"), DataSourceOf(api));
    }

    [Fact]
    public void RelativeDataSourceResolvesUnderTheSharedDirectory()
    {
        var resolved = SqliteStoragePath.Resolve("Data Source=iroute.db", sharedBase);

        Assert.Equal(Path.Combine(sharedBase, "iroute.db"), DataSourceOf(resolved));
    }

    [Fact]
    public void AbsoluteDataSourceIsPreserved()
    {
        var absolute = Path.Combine(Path.GetTempPath(), "explicit.db");

        var resolved = SqliteStoragePath.Resolve($"Data Source={absolute}", sharedBase);

        Assert.Equal(absolute, DataSourceOf(resolved));
    }

    [Fact]
    public void InMemoryDataSourceIsPreserved()
    {
        var resolved = SqliteStoragePath.Resolve("Data Source=:memory:", sharedBase);

        Assert.Equal(":memory:", DataSourceOf(resolved));
    }

    [Fact]
    public void AdditionalKeywordsSurviveResolution()
    {
        var resolved = SqliteStoragePath.Resolve("Data Source=iroute.db;Cache=Shared", sharedBase);

        var builder = new SqliteConnectionStringBuilder(resolved);
        Assert.Equal(Path.Combine(sharedBase, "iroute.db"), builder.DataSource);
        Assert.Equal(SqliteCacheMode.Shared, builder.Cache);
    }

    [Fact]
    public void ResolvingCreatesTheSharedDirectorySoSqliteCanOpenTheFile()
    {
        // SQLite creates a missing database file but not its parent directory, so a first run
        // against a fresh machine fails at startup unless resolution creates the directory.
        Assert.False(Directory.Exists(sharedBase));

        SqliteStoragePath.Resolve("Data Source=iroute.db", sharedBase);

        Assert.True(Directory.Exists(sharedBase));
    }

    [Fact]
    public void ResolvedDatabaseIsActuallyOpenable()
    {
        var resolved = SqliteStoragePath.Resolve("Data Source=iroute.db", sharedBase);

        using var connection = new SqliteConnection(resolved);
        connection.Open();

        Assert.Equal(System.Data.ConnectionState.Open, connection.State);
    }

    [Fact]
    public void NonSqliteProvidersAreLeftUntouched()
    {
        const string postgres = "Host=postgres;Port=5432;Database=iroute;Username=iroute";

        Assert.Equal(postgres, SqliteStoragePath.ResolveForProvider("Postgres", postgres, sharedBase));
    }

    [Fact]
    public void SqliteProviderIsResolvedThroughTheProviderEntryPoint()
    {
        var resolved = SqliteStoragePath.ResolveForProvider("Sqlite", "Data Source=iroute.db", sharedBase);

        Assert.Equal(Path.Combine(sharedBase, "iroute.db"), DataSourceOf(resolved));
    }
}
