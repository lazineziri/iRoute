using iRoute.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace iRoute.Data;

public sealed class SchemaMigrationManager(
    IDbContextFactory<IRouteDbContext> contextFactory)
{
    public const string InitialDatabase = "0";

    public async Task<SchemaMigrationStatus> GetStatusAsync(
        CancellationToken cancellationToken = default)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var known = context.Database.GetMigrations().ToArray();
        var applied = (await context.Database.GetAppliedMigrationsAsync(cancellationToken)).ToArray();
        var pending = known.Except(applied, StringComparer.Ordinal).ToArray();
        var unknown = applied.Except(known, StringComparer.Ordinal).ToArray();
        return new SchemaMigrationStatus(
            context.Database.ProviderName ?? "unknown",
            applied.LastOrDefault(),
            applied,
            pending,
            unknown);
    }

    public async Task<SchemaMigrationStatus> UpgradeAsync(
        string? targetMigration = null,
        CancellationToken cancellationToken = default)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var known = context.Database.GetMigrations().ToArray();
        var applied = (await context.Database.GetAppliedMigrationsAsync(cancellationToken)).ToArray();
        EnsureNoUnknownMigrations(known, applied);
        var target = targetMigration ?? known.LastOrDefault()
            ?? throw new InvalidOperationException("The runtime contains no schema migrations.");
        var targetIndex = Array.IndexOf(known, target);
        if (targetIndex < 0)
        {
            throw new ArgumentException($"Unknown target migration '{target}'.", nameof(targetMigration));
        }

        var currentIndex = applied.Length == 0 ? -1 : Array.IndexOf(known, applied[^1]);
        if (targetIndex < currentIndex)
        {
            throw new InvalidOperationException(
                $"Target '{target}' precedes the current schema. Use the explicit rollback command.");
        }

        if (targetIndex != currentIndex)
        {
            await context.GetService<IMigrator>().MigrateAsync(target, cancellationToken);
        }

        return await GetStatusAsync(cancellationToken);
    }

    public async Task<SchemaMigrationStatus> RollbackAsync(
        string targetMigration,
        bool confirmed,
        CancellationToken cancellationToken = default)
    {
        if (!confirmed)
        {
            throw new InvalidOperationException(
                "Schema rollback can destroy data and requires the explicit --confirm flag.");
        }

        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var known = context.Database.GetMigrations().ToArray();
        var applied = (await context.Database.GetAppliedMigrationsAsync(cancellationToken)).ToArray();
        EnsureNoUnknownMigrations(known, applied);
        if (applied.Length == 0)
        {
            throw new InvalidOperationException("The database has no applied migrations to roll back.");
        }

        var targetIndex = string.Equals(targetMigration, InitialDatabase, StringComparison.Ordinal)
            ? -1
            : Array.IndexOf(known, targetMigration);
        if (targetIndex < 0 && !string.Equals(targetMigration, InitialDatabase, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                $"Unknown rollback target '{targetMigration}'.",
                nameof(targetMigration));
        }

        var currentIndex = Array.IndexOf(known, applied[^1]);
        if (targetIndex >= currentIndex)
        {
            throw new InvalidOperationException(
                $"Rollback target '{targetMigration}' must precede the current migration '{applied[^1]}'.");
        }

        await context.GetService<IMigrator>().MigrateAsync(targetMigration, cancellationToken);
        return await GetStatusAsync(cancellationToken);
    }

    private static void EnsureNoUnknownMigrations(
        IReadOnlyCollection<string> known,
        IReadOnlyCollection<string> applied)
    {
        var unknown = applied.Except(known, StringComparer.Ordinal).ToArray();
        if (unknown.Length > 0)
        {
            throw new InvalidOperationException(
                $"The database contains migrations unknown to this runtime: {string.Join(", ", unknown)}.");
        }
    }
}
