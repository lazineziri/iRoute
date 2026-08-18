using Microsoft.Data.Sqlite;

namespace iRoute.Data;

/// <summary>
/// Resolves relative SQLite data sources to a fixed shared directory.
/// </summary>
/// <remarks>
/// Hosts are started from different working directories (<c>dotnet run --project</c> uses the
/// project folder), so a relative <c>Data Source</c> would give the API and the worker separate
/// databases and queued executions would never be processed.
/// </remarks>
public static class SqliteStoragePath
{
    /// <summary>
    /// Per-user directory shared by every iRoute host on the machine.
    /// </summary>
    public static string DefaultSharedDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(
            Environment.SpecialFolder.LocalApplicationData,
            Environment.SpecialFolderOption.Create),
        "iRoute");

    public static string ResolveForProvider(
        string storageProvider,
        string connectionString,
        string? sharedDirectory = null) =>
        string.Equals(storageProvider, "Sqlite", StringComparison.OrdinalIgnoreCase)
            ? Resolve(connectionString, sharedDirectory)
            : connectionString;

    public static string Resolve(string connectionString, string? sharedDirectory = null)
    {
        ArgumentNullException.ThrowIfNull(connectionString);

        var builder = new SqliteConnectionStringBuilder(connectionString);
        var dataSource = builder.DataSource;

        if (string.IsNullOrWhiteSpace(dataSource) ||
            IsInMemory(dataSource) ||
            Path.IsPathRooted(dataSource))
        {
            return connectionString;
        }

        var directory = sharedDirectory ?? DefaultSharedDirectory;

        // SQLite creates a missing database file but not its parent directory.
        Directory.CreateDirectory(directory);

        builder.DataSource = Path.Combine(directory, dataSource);
        return builder.ToString();
    }

    private static bool IsInMemory(string dataSource) =>
        dataSource.Equals(":memory:", StringComparison.OrdinalIgnoreCase) ||
        dataSource.StartsWith("file::memory:", StringComparison.OrdinalIgnoreCase);
}
