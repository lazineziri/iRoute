using System.Data;
using System.Text.Json;
using iRoute.Common;
using Microsoft.EntityFrameworkCore;

namespace iRoute.Data;

public sealed class EfExecutionWorkStore(
    IDbContextFactory<IRouteDbContext> contextFactory) : IExecutionWorkStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<ExecutionWorkItem> EnqueueAsync(
        Guid executionId,
        ExecutionStatus expectedStatus,
        DateTimeOffset queuedAt,
        CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await context.Database.BeginTransactionAsync(
            IsolationLevel.Serializable,
            cancellationToken);
        var execution = await context.Executions.SingleOrDefaultAsync(
            item => item.ExecutionId == executionId,
            cancellationToken) ?? throw new KeyNotFoundException($"Execution '{executionId}' was not found.");
        var existing = await context.ExecutionWorkItems.SingleOrDefaultAsync(
            item => item.ExecutionId == executionId,
            cancellationToken);
        if (existing is not null)
        {
            if (existing.State is ExecutionWorkState.Pending or ExecutionWorkState.Leased)
            {
                await transaction.CommitAsync(cancellationToken);
                return ToContract(existing);
            }

            throw new InvalidOperationException(
                $"Execution work '{executionId}' cannot be enqueued from {existing.State}.");
        }

        if (execution.Status != expectedStatus)
        {
            throw new InvalidOperationException(
                $"Execution '{executionId}' cannot be queued from {execution.Status}; expected {expectedStatus}.");
        }

        execution.Status = ExecutionStatus.Queued;
        execution.UpdatedAtUnixMilliseconds = queuedAt.ToUnixTimeMilliseconds();
        var entity = new ExecutionWorkItemEntity
        {
            ExecutionId = executionId,
            State = ExecutionWorkState.Pending,
            AvailableAtUnixMilliseconds = queuedAt.ToUnixTimeMilliseconds()
        };
        context.ExecutionWorkItems.Add(entity);
        await context.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return ToContract(entity);
    }

    public async Task<ExecutionLease?> TryClaimAsync(
        string workerId,
        DateTimeOffset claimedAt,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken)
    {
        EnsureWorker(workerId);
        EnsureLeaseDuration(leaseDuration);
        var now = claimedAt.ToUnixTimeMilliseconds();
        for (var scan = 0; scan < 8; scan++)
        {
            await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
            var candidate = await context.ExecutionWorkItems
                .AsNoTracking()
                .Where(item =>
                    (item.State == ExecutionWorkState.Pending && item.AvailableAtUnixMilliseconds <= now) ||
                    (item.State == ExecutionWorkState.Leased && item.LeaseExpiresAtUnixMilliseconds <= now))
                .OrderBy(item => item.AvailableAtUnixMilliseconds)
                .ThenBy(item => item.ExecutionId)
                .FirstOrDefaultAsync(cancellationToken);
            if (candidate is null)
            {
                return null;
            }

            var leaseToken = Guid.CreateVersion7();
            var expiresAt = claimedAt.Add(leaseDuration);
            var expires = expiresAt.ToUnixTimeMilliseconds();
            var updated = await context.ExecutionWorkItems
                .Where(item =>
                    item.ExecutionId == candidate.ExecutionId &&
                    ((item.State == ExecutionWorkState.Pending && item.AvailableAtUnixMilliseconds <= now) ||
                     (item.State == ExecutionWorkState.Leased && item.LeaseExpiresAtUnixMilliseconds <= now)))
                .ExecuteUpdateAsync(
                    setters => setters
                        .SetProperty(item => item.State, ExecutionWorkState.Leased)
                        .SetProperty(item => item.DeliveryAttempt, item => item.DeliveryAttempt + 1)
                        .SetProperty(item => item.LeaseOwner, workerId)
                        .SetProperty(item => item.LeaseToken, leaseToken)
                        .SetProperty(item => item.LeaseExpiresAtUnixMilliseconds, expires)
                        .SetProperty(item => item.HeartbeatAtUnixMilliseconds, now)
                        .SetProperty(item => item.CompletedAtUnixMilliseconds, (long?)null),
                    cancellationToken);
            if (updated == 1)
            {
                return new ExecutionLease(
                    candidate.ExecutionId,
                    workerId,
                    leaseToken,
                    checked(candidate.DeliveryAttempt + 1),
                    expiresAt);
            }
        }

        return null;
    }

    public async Task<ExecutionLeaseHeartbeat> RenewAsync(
        ExecutionLease lease,
        DateTimeOffset renewedAt,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken)
    {
        EnsureLeaseDuration(leaseDuration);
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var now = renewedAt.ToUnixTimeMilliseconds();
        var expiresAt = renewedAt.Add(leaseDuration);
        var expires = expiresAt.ToUnixTimeMilliseconds();
        var updated = await Owned(context, lease)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(item => item.LeaseExpiresAtUnixMilliseconds, expires)
                    .SetProperty(item => item.HeartbeatAtUnixMilliseconds, now),
                cancellationToken);
        if (updated != 1)
        {
            return new(false, false);
        }

        var cancellationRequested = await context.Executions
            .AsNoTracking()
            .Where(item => item.ExecutionId == lease.ExecutionId)
            .Select(item => item.CancellationRequestedAtUnixMilliseconds != null)
            .SingleAsync(cancellationToken);
        return new(true, cancellationRequested, expiresAt);
    }

    public async Task<bool> CompleteAsync(
        ExecutionLease lease,
        DateTimeOffset completedAt,
        CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        return await Owned(context, lease).ExecuteUpdateAsync(
            setters => setters
                .SetProperty(item => item.State, ExecutionWorkState.Completed)
                .SetProperty(item => item.LeaseOwner, (string?)null)
                .SetProperty(item => item.LeaseToken, (Guid?)null)
                .SetProperty(item => item.LeaseExpiresAtUnixMilliseconds, (long?)null)
                .SetProperty(item => item.HeartbeatAtUnixMilliseconds, (long?)null)
                .SetProperty(item => item.CompletedAtUnixMilliseconds, completedAt.ToUnixTimeMilliseconds()),
            cancellationToken) == 1;
    }

    public async Task<bool> AbandonAsync(
        ExecutionLease lease,
        DateTimeOffset availableAt,
        CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        return await Owned(context, lease).ExecuteUpdateAsync(
            setters => setters
                .SetProperty(item => item.State, ExecutionWorkState.Pending)
                .SetProperty(item => item.AvailableAtUnixMilliseconds, availableAt.ToUnixTimeMilliseconds())
                .SetProperty(item => item.LeaseOwner, (string?)null)
                .SetProperty(item => item.LeaseToken, (Guid?)null)
                .SetProperty(item => item.LeaseExpiresAtUnixMilliseconds, (long?)null)
                .SetProperty(item => item.HeartbeatAtUnixMilliseconds, (long?)null),
            cancellationToken) == 1;
    }

    public async Task<bool> CancelPendingAsync(
        Guid executionId,
        DateTimeOffset cancelledAt,
        Problem problem,
        CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await context.Database.BeginTransactionAsync(
            IsolationLevel.Serializable,
            cancellationToken);
        var work = await context.ExecutionWorkItems.SingleOrDefaultAsync(
            item => item.ExecutionId == executionId && item.State == ExecutionWorkState.Pending,
            cancellationToken);
        if (work is null)
        {
            await transaction.CommitAsync(cancellationToken);
            return false;
        }

        var execution = await context.Executions.SingleAsync(
            item => item.ExecutionId == executionId,
            cancellationToken);
        if (execution.Status != ExecutionStatus.Queued)
        {
            await transaction.CommitAsync(cancellationToken);
            return false;
        }

        var timestamp = cancelledAt.ToUnixTimeMilliseconds();
        work.State = ExecutionWorkState.Cancelled;
        work.CompletedAtUnixMilliseconds = timestamp;
        execution.Status = ExecutionStatus.Cancelled;
        execution.UpdatedAtUnixMilliseconds = timestamp;
        execution.CancellationRequestedAtUnixMilliseconds ??= timestamp;
        execution.ErrorJson = JsonSerializer.Serialize(problem, JsonOptions);
        await context.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return true;
    }

    public async Task<ExecutionWorkItem?> GetAsync(
        Guid executionId,
        CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var entity = await context.ExecutionWorkItems
            .AsNoTracking()
            .SingleOrDefaultAsync(item => item.ExecutionId == executionId, cancellationToken);
        return entity is null ? null : ToContract(entity);
    }

    private static IQueryable<ExecutionWorkItemEntity> Owned(
        IRouteDbContext context,
        ExecutionLease lease) =>
        context.ExecutionWorkItems.Where(item =>
            item.ExecutionId == lease.ExecutionId &&
            item.State == ExecutionWorkState.Leased &&
            item.LeaseToken == lease.LeaseToken &&
            item.LeaseOwner == lease.WorkerId);

    private static ExecutionWorkItem ToContract(ExecutionWorkItemEntity entity) => new(
        entity.ExecutionId,
        entity.State,
        DateTimeOffset.FromUnixTimeMilliseconds(entity.AvailableAtUnixMilliseconds),
        entity.DeliveryAttempt,
        entity.LeaseOwner,
        entity.LeaseToken,
        entity.LeaseExpiresAtUnixMilliseconds is { } expires
            ? DateTimeOffset.FromUnixTimeMilliseconds(expires)
            : null,
        entity.HeartbeatAtUnixMilliseconds is { } heartbeat
            ? DateTimeOffset.FromUnixTimeMilliseconds(heartbeat)
            : null,
        entity.CompletedAtUnixMilliseconds is { } completed
            ? DateTimeOffset.FromUnixTimeMilliseconds(completed)
            : null);

    private static void EnsureWorker(string workerId)
    {
        if (string.IsNullOrWhiteSpace(workerId))
        {
            throw new ArgumentException("WorkerId is required.", nameof(workerId));
        }
    }

    private static void EnsureLeaseDuration(TimeSpan leaseDuration)
    {
        if (leaseDuration <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(leaseDuration), "Lease duration must be positive.");
        }
    }
}
