namespace iRoute.Common;

public sealed record SchemaMigrationStatus(
    string Provider,
    string? CurrentMigration,
    IReadOnlyList<string> AppliedMigrations,
    IReadOnlyList<string> PendingMigrations,
    IReadOnlyList<string> UnknownAppliedMigrations)
{
    public bool IsCurrent => PendingMigrations.Count == 0 && UnknownAppliedMigrations.Count == 0;
}
