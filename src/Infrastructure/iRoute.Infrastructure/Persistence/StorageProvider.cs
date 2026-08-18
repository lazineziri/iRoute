namespace iRoute.Infrastructure;

/// <summary>
/// The storage providers iRoute supports.
/// </summary>
/// <remarks>
/// There are two, deliberately. PostgreSQL is the deployment target: it is the only provider that
/// supports more than one execution worker, because durable leases, event sequencing and gateway
/// circuit state are all coordinated through it. SQLite is for single-node development and
/// evaluation. Both share one schema and one migration history.
/// </remarks>
public sealed record StorageProvider
{
    public const string Sqlite = "Sqlite";
    public const string Postgres = "Postgres";

    private StorageProvider(string name) => Name = name;

    public string Name { get; }

    public bool IsSqlite => Name == Sqlite;

    /// <summary>
    /// SQLite serialises all writes through one file and cannot coordinate workers across hosts.
    /// </summary>
    public bool SupportsMultipleWorkers => !IsSqlite;

    public static StorageProvider Parse(string? configuredProvider)
    {
        var provider = string.IsNullOrWhiteSpace(configuredProvider) ? Sqlite : configuredProvider.Trim();

        if (string.Equals(provider, Sqlite, StringComparison.OrdinalIgnoreCase))
        {
            return new StorageProvider(Sqlite);
        }

        if (string.Equals(provider, Postgres, StringComparison.OrdinalIgnoreCase))
        {
            return new StorageProvider(Postgres);
        }

        if (string.Equals(provider, "Memory", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "Storage:Provider=Memory is no longer available. It kept no durable record, so an " +
                "execution could not survive a restart and approvals, leases and artifacts were lost " +
                $"with the process. Use {Sqlite} for single-node development or {Postgres} to deploy.");
        }

        throw new InvalidOperationException(
            $"Unsupported storage provider '{provider}'. Use {Sqlite} for single-node development " +
            $"or {Postgres} to deploy.");
    }
}
