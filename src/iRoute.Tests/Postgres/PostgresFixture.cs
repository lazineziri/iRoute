using iRoute.Tests.Executions;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Xunit;

namespace iRoute.Tests.Postgres;

internal sealed class PostgresFixture : IAsyncDisposable
{
    private readonly string connection;
    private readonly string schema;
    private readonly ExecutionFixture fixture;
    private PostgresFixture(string connection, string schema, ExecutionFixture fixture)
    { this.connection = connection; this.schema = schema; this.fixture = fixture; }

    public IServiceProvider Services => fixture.Services;
    public static bool Enabled => !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("IROUTE_POSTGRES_TEST_CONNECTION"));

    public static async Task<PostgresFixture> CreateAsync(Action<IServiceCollection>? configure = null, string? migrationTarget = null)
    {
        var connection = Environment.GetEnvironmentVariable("IROUTE_POSTGRES_TEST_CONNECTION")
            ?? throw new InvalidOperationException("An explicitly configured disposable PostgreSQL test database is required.");
        var schema = "iroute_test_" + Guid.NewGuid().ToString("N");
        await ExecuteSchemaAsync(connection, $"CREATE SCHEMA \"{schema}\"", TestContext.Current.CancellationToken);
        try
        {
            var scoped = new NpgsqlConnectionStringBuilder(connection) { SearchPath = schema, Pooling = false }.ConnectionString;
            var fixture = await ExecutionFixture.CreateAsync(configure: configure, postgresConnectionString: scoped, migrationTarget: migrationTarget);
            return new(connection, schema, fixture);
        }
        catch
        {
            await ExecuteSchemaAsync(connection, $"DROP SCHEMA \"{schema}\" CASCADE", CancellationToken.None);
            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        await fixture.DisposeAsync();
        // The only deletion target is this fixture's freshly generated, private schema.
        await ExecuteSchemaAsync(connection, $"DROP SCHEMA \"{schema}\" CASCADE", CancellationToken.None);
    }

    private static async Task ExecuteSchemaAsync(string connectionString, string sql, CancellationToken token)
    {
        await using var admin = new NpgsqlConnection(connectionString);
        await admin.OpenAsync(token);
        await using var command = new NpgsqlCommand(sql, admin);
        await command.ExecuteNonQueryAsync(token);
    }
}
