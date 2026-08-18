using System.Data;
using System.Runtime.CompilerServices;
using System.Text.Json;
using iRoute.Common;
using Microsoft.EntityFrameworkCore;

namespace iRoute.Data;

public sealed class EfExecutionStore(
    IDbContextFactory<IRouteDbContext> contextFactory,
    IExecutionFence fence) : IExecutionStore
{
    /// <summary>
    /// Verifies that the lease in scope still owns the execution. Runs inside the caller's
    /// transaction so ownership cannot change between the check and the write.
    /// </summary>
    private async Task EnsureLeaseOwnsAsync(
        IRouteDbContext context,
        Guid executionId,
        CancellationToken cancellationToken)
    {
        await ExecutionLeaseGuard.EnsureOwnsAsync(
            context,
            fence,
            executionId,
            cancellationToken);
    }

    public async Task<ExecutionSubmission?> FindByIdempotencyKeyAsync(
        string tenantId,
        string key,
        CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var entity = await context.Executions
            .AsNoTracking()
            .SingleOrDefaultAsync(
                x => x.TenantId == tenantId && x.IdempotencyKey == key,
                cancellationToken);
        return entity is null
            ? null
            : new ExecutionSubmission(PersistenceMapping.ToContract(entity), entity.InputFingerprint);
    }

    public async Task<ExecutionSnapshot?> GetAsync(Guid executionId, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var entity = await context.Executions
            .AsNoTracking()
            .SingleOrDefaultAsync(x => x.ExecutionId == executionId, cancellationToken);
        return entity is null ? null : PersistenceMapping.ToContract(entity);
    }

    public async Task CreateAsync(
        ExecutionSnapshot execution,
        string? idempotencyKey,
        string? inputFingerprint,
        CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        context.Executions.Add(
            PersistenceMapping.ToEntity(execution, idempotencyKey, inputFingerprint));
        try
        {
            await context.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException exception)
            when (!string.IsNullOrWhiteSpace(idempotencyKey) && IsUniqueViolation(exception))
        {
            // A concurrent submit with the same key won the race. Surface a typed conflict so the
            // caller can re-read and answer with the execution that was actually created.
            throw new IdempotencyConflictException(execution.TenantId, idempotencyKey);
        }
    }

    private static bool IsUniqueViolation(DbUpdateException exception) =>
        exception.InnerException is { } inner &&
        (inner.Message.Contains("UNIQUE constraint failed", StringComparison.OrdinalIgnoreCase) ||
            inner.Message.Contains("duplicate key value", StringComparison.OrdinalIgnoreCase) ||
            (inner.GetType().Name == "PostgresException" &&
                inner.GetType().GetProperty("SqlState")?.GetValue(inner) as string == "23505"));

    public async Task UpdateAsync(ExecutionSnapshot execution, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
        await EnsureLeaseOwnsAsync(context, execution.ExecutionId, cancellationToken);
        var entity = await context.Executions.SingleAsync(
            x => x.ExecutionId == execution.ExecutionId,
            cancellationToken);
        PersistenceMapping.Apply(execution, entity);
        await context.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task<ExecutionSnapshot?> TryTransitionAsync(
        Guid executionId,
        ExecutionStatus expectedStatus,
        ExecutionStatus targetStatus,
        DateTimeOffset updatedAt,
        CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
        await EnsureLeaseOwnsAsync(context, executionId, cancellationToken);
        var written = await context.Executions
            .Where(item => item.ExecutionId == executionId && item.Status == expectedStatus)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(item => item.Status, targetStatus)
                    .SetProperty(
                        item => item.UpdatedAtUnixMilliseconds,
                        updatedAt.ToUnixTimeMilliseconds()),
                cancellationToken);
        if (written != 1)
        {
            await transaction.CommitAsync(cancellationToken);
            return null;
        }

        var entity = await context.Executions
            .AsNoTracking()
            .SingleAsync(item => item.ExecutionId == executionId, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return PersistenceMapping.ToContract(entity);
    }

    public async Task<bool> TryRequestCancellationAsync(
        Guid executionId,
        DateTimeOffset requestedAt,
        CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

        // Touch only the cancellation column so a concurrent worker transition cannot be reverted
        // and a recorded outcome cannot be erased.
        var written = await context.Executions
            .Where(x => x.ExecutionId == executionId
                && x.CancellationRequestedAtUnixMilliseconds == null
                && !ExecutionStatusFacts.TerminalStatuses.Contains(x.Status))
            .ExecuteUpdateAsync(
                setters => setters.SetProperty(
                    x => x.CancellationRequestedAtUnixMilliseconds,
                    requestedAt.ToUnixTimeMilliseconds()),
                cancellationToken);

        if (written > 0)
        {
            return true;
        }

        // Nothing was written: the execution is missing, already terminal, or already cancelled.
        var status = await context.Executions
            .AsNoTracking()
            .Where(x => x.ExecutionId == executionId)
            .Select(x => (ExecutionStatus?)x.Status)
            .SingleOrDefaultAsync(cancellationToken);

        return status is not null && !ExecutionStatusFacts.IsTerminal(status.Value);
    }

    public async IAsyncEnumerable<ExecutionEvent> ReadEventsAsync(
        Guid executionId,
        long afterSequence,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var entities = await context.ExecutionEvents
            .AsNoTracking()
            .Where(x => x.ExecutionId == executionId && x.Sequence > afterSequence)
            .OrderBy(x => x.Sequence)
            .ToListAsync(cancellationToken);
        foreach (var entity in entities)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return PersistenceMapping.ToContract(entity);
        }
    }

    public async Task<ExecutionEvent> AppendEventAsync(
        Guid executionId,
        string eventType,
        DateTimeOffset occurredAt,
        JsonElement data,
        CancellationToken cancellationToken)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
                var isPostgres = context.Database.ProviderName?.Contains(
                    "Npgsql",
                    StringComparison.Ordinal) is true;
                await using var transaction = await context.Database.BeginTransactionAsync(
                    isPostgres ? IsolationLevel.ReadCommitted : IsolationLevel.Serializable,
                    cancellationToken);
                if (isPostgres)
                {
                    _ = await context.Executions
                        .FromSqlInterpolated(
                            $"SELECT * FROM \"Executions\" WHERE \"ExecutionId\" = {executionId} FOR UPDATE")
                        .AsNoTracking()
                        .SingleAsync(cancellationToken);
                }

                await EnsureLeaseOwnsAsync(context, executionId, cancellationToken);

                var previousSequence = await context.ExecutionEvents
                    .Where(x => x.ExecutionId == executionId)
                    .MaxAsync(x => (long?)x.Sequence, cancellationToken) ?? 0;
                var executionEvent = new ExecutionEvent(
                    checked(previousSequence + 1),
                    executionId,
                    eventType,
                    occurredAt,
                    data.Clone());
                context.ExecutionEvents.Add(PersistenceMapping.ToEntity(executionEvent));
                await context.SaveChangesAsync(cancellationToken);
                await transaction.CommitAsync(cancellationToken);
                return executionEvent;
            }
            catch (Exception exception) when (attempt < 5 && IsEventSequenceContention(exception))
            {
                await Task.Delay(TimeSpan.FromMilliseconds(attempt * 5), cancellationToken);
            }
        }
    }

    private static bool IsEventSequenceContention(Exception exception) => exception switch
    {
        DbUpdateException { InnerException: Npgsql.PostgresException { SqlState: "23505" or "40001" } } => true,
        Npgsql.PostgresException { SqlState: "23505" or "40001" } => true,
        Microsoft.Data.Sqlite.SqliteException { SqliteErrorCode: 5 or 6 or 19 } => true,
        _ => false
    };
}
