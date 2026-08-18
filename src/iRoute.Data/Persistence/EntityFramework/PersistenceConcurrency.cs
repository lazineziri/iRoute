using iRoute.Common;
using Microsoft.EntityFrameworkCore;

namespace iRoute.Data;

internal static class ExecutionLeaseGuard
{
    public static async Task EnsureOwnsAsync(
        IRouteDbContext context,
        IExecutionFence fence,
        Guid executionId,
        CancellationToken cancellationToken)
    {
        if (fence.CurrentToken is not { } token)
        {
            return;
        }

        // This guarded no-op update takes the same row lock used by lease takeover. Keeping that
        // lock until the caller commits prevents ownership from changing between the fence check
        // and the protected write.
        var owned = await context.ExecutionWorkItems
            .Where(item => item.ExecutionId == executionId
                && item.LeaseToken == token
                && item.State == ExecutionWorkState.Leased)
            .ExecuteUpdateAsync(
                setters => setters.SetProperty(item => item.LeaseToken, item => item.LeaseToken),
                cancellationToken);
        if (owned != 1)
        {
            throw new LeaseFencedException(executionId);
        }
    }
}

internal static class PersistenceContention
{
    public static async Task<T> RetryAsync<T>(
        Func<Task<T>> action,
        CancellationToken cancellationToken)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await action();
            }
            catch (Exception exception) when (attempt < 6 && IsRetryable(exception))
            {
                await Task.Delay(TimeSpan.FromMilliseconds(attempt * 5), cancellationToken);
            }
        }
    }

    private static bool IsRetryable(Exception exception) => exception switch
    {
        DbUpdateException { InnerException: Npgsql.PostgresException { SqlState: "23505" or "40001" or "40P01" } } => true,
        Npgsql.PostgresException { SqlState: "23505" or "40001" or "40P01" } => true,
        Microsoft.Data.Sqlite.SqliteException { SqliteErrorCode: 5 or 6 } => true,
        Microsoft.Data.Sqlite.SqliteException { SqliteExtendedErrorCode: 1555 or 2067 } => true,
        _ => false
    };
}
