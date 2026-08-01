using System.Collections.Concurrent;
using iRoute.Contracts;
using iRoute.Core;

namespace iRoute.Infrastructure;

public sealed class InMemoryExecutionWorkStore(InMemoryExecutionStore executions) : IExecutionWorkStore
{
    private readonly ConcurrentDictionary<Guid, ExecutionWorkItem> _items = new();
    private readonly object _gate = new();

    public Task<ExecutionWorkItem> EnqueueAsync(
        Guid executionId,
        ExecutionStatus expectedStatus,
        DateTimeOffset queuedAt,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            if (_items.TryGetValue(executionId, out var existing))
            {
                if (existing.State is ExecutionWorkState.Pending or ExecutionWorkState.Leased)
                {
                    return Task.FromResult(existing);
                }

                throw new InvalidOperationException(
                    $"Execution work '{executionId}' cannot be enqueued from {existing.State}.");
            }

            executions.Queue(executionId, expectedStatus, queuedAt);
            var item = new ExecutionWorkItem(
                executionId,
                ExecutionWorkState.Pending,
                queuedAt,
                0);
            if (!_items.TryAdd(executionId, item))
            {
                throw new InvalidOperationException($"Execution work '{executionId}' already exists.");
            }

            return Task.FromResult(item);
        }
    }

    public Task<ExecutionLease?> TryClaimAsync(
        string workerId,
        DateTimeOffset claimedAt,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        EnsureWorker(workerId);
        EnsureLeaseDuration(leaseDuration);
        lock (_gate)
        {
            var candidate = _items.Values
                .Where(item =>
                    (item.State == ExecutionWorkState.Pending && item.AvailableAt <= claimedAt) ||
                    (item.State == ExecutionWorkState.Leased && item.LeaseExpiresAt <= claimedAt))
                .OrderBy(item => item.AvailableAt)
                .ThenBy(item => item.ExecutionId)
                .FirstOrDefault();
            if (candidate is null)
            {
                return Task.FromResult<ExecutionLease?>(null);
            }

            var token = Guid.CreateVersion7();
            var expiresAt = claimedAt.Add(leaseDuration);
            var attempt = checked(candidate.DeliveryAttempt + 1);
            _items[candidate.ExecutionId] = candidate with
            {
                State = ExecutionWorkState.Leased,
                DeliveryAttempt = attempt,
                LeaseOwner = workerId,
                LeaseToken = token,
                LeaseExpiresAt = expiresAt,
                HeartbeatAt = claimedAt,
                CompletedAt = null
            };
            return Task.FromResult<ExecutionLease?>(new(
                candidate.ExecutionId,
                workerId,
                token,
                attempt,
                expiresAt));
        }
    }

    public Task<ExecutionLeaseHeartbeat> RenewAsync(
        ExecutionLease lease,
        DateTimeOffset renewedAt,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        EnsureLeaseDuration(leaseDuration);
        lock (_gate)
        {
            if (!_items.TryGetValue(lease.ExecutionId, out var current) || !Owns(current, lease))
            {
                return Task.FromResult(new ExecutionLeaseHeartbeat(false, false));
            }

            var expiresAt = renewedAt.Add(leaseDuration);
            _items[lease.ExecutionId] = current with
            {
                LeaseExpiresAt = expiresAt,
                HeartbeatAt = renewedAt
            };
            var snapshot = executions.GetAsync(lease.ExecutionId, cancellationToken)
                .GetAwaiter()
                .GetResult();
            return Task.FromResult(new ExecutionLeaseHeartbeat(
                true,
                snapshot?.CancellationRequestedAt is not null,
                expiresAt));
        }
    }

    public Task<bool> CompleteAsync(
        ExecutionLease lease,
        DateTimeOffset completedAt,
        CancellationToken cancellationToken) =>
        MutateOwnedAsync(
            lease,
            current => current with
            {
                State = ExecutionWorkState.Completed,
                LeaseOwner = null,
                LeaseToken = null,
                LeaseExpiresAt = null,
                HeartbeatAt = null,
                CompletedAt = completedAt
            },
            cancellationToken);

    public Task<bool> AbandonAsync(
        ExecutionLease lease,
        DateTimeOffset availableAt,
        CancellationToken cancellationToken) =>
        MutateOwnedAsync(
            lease,
            current => current with
            {
                State = ExecutionWorkState.Pending,
                AvailableAt = availableAt,
                LeaseOwner = null,
                LeaseToken = null,
                LeaseExpiresAt = null,
                HeartbeatAt = null
            },
            cancellationToken);

    public Task<bool> CancelPendingAsync(
        Guid executionId,
        DateTimeOffset cancelledAt,
        Problem problem,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            if (!_items.TryGetValue(executionId, out var current) || current.State != ExecutionWorkState.Pending)
            {
                return Task.FromResult(false);
            }

            if (!executions.CancelQueued(executionId, cancelledAt, problem))
            {
                return Task.FromResult(false);
            }

            _items[executionId] = current with
            {
                State = ExecutionWorkState.Cancelled,
                CompletedAt = cancelledAt
            };
            return Task.FromResult(true);
        }
    }

    public Task<ExecutionWorkItem?> GetAsync(Guid executionId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(_items.GetValueOrDefault(executionId));
    }

    private Task<bool> MutateOwnedAsync(
        ExecutionLease lease,
        Func<ExecutionWorkItem, ExecutionWorkItem> update,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            if (!_items.TryGetValue(lease.ExecutionId, out var current) || !Owns(current, lease))
            {
                return Task.FromResult(false);
            }

            _items[lease.ExecutionId] = update(current);
            return Task.FromResult(true);
        }
    }

    private static bool Owns(ExecutionWorkItem item, ExecutionLease lease) =>
        item.State == ExecutionWorkState.Leased &&
        item.LeaseToken == lease.LeaseToken &&
        string.Equals(item.LeaseOwner, lease.WorkerId, StringComparison.Ordinal);

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
