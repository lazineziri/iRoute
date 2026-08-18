namespace iRoute.Common;

public sealed record StorageOptions
{
    public string Provider { get; init; } = "Sqlite";
    public bool AutoInitialize { get; init; } = true;
}
