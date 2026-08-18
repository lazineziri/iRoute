using System.Text.Json;

namespace iRoute.Common;

/// <summary>
/// An execution recorded against an idempotency key, with the fingerprint of the request that
/// created it so a replay carrying a different payload can be told apart from a genuine retry.
/// </summary>
public sealed record ExecutionSubmission(ExecutionSnapshot Execution, string? InputFingerprint);

/// <summary>
/// Thrown when an idempotency key is already recorded for the tenant. Concurrent submits with the
/// same key race, so callers must treat this as "someone else won" and re-read, not as an error.
/// </summary>
public sealed class IdempotencyConflictException(string tenantId, string idempotencyKey)
    : Exception($"Idempotency key '{idempotencyKey}' already exists for tenant '{tenantId}'.")
{
    public string TenantId { get; } = tenantId;
    public string IdempotencyKey { get; } = idempotencyKey;
}

/// <summary>
/// Thrown when an idempotency key is replayed with a different request payload. Unlike
/// <see cref="IdempotencyConflictException"/> this is a client error, not a race: answering with
/// the original execution would hide the mistake, so it is reported instead.
/// </summary>
public sealed class IdempotencyKeyReusedException(string idempotencyKey)
    : Exception($"Idempotency key '{idempotencyKey}' was already used for a different request payload.")
{
    public string IdempotencyKey { get; } = idempotencyKey;
}

/// <summary>
/// Thrown when a write is attempted under a lease that no longer owns the execution.
/// </summary>
public sealed class LeaseFencedException(Guid executionId)
    : Exception($"The lease held for execution '{executionId}' is no longer the owning lease.")
{
    public Guid ExecutionId { get; } = executionId;
}

/// <summary>
/// Carries the lease token of the work currently being processed, so durable writes can verify
/// ownership in the same transaction that performs them.
/// </summary>
/// <remarks>
/// A worker whose lease expired can still be mid-execution when another worker takes over. Local
/// cancellation alone cannot prevent that, because it depends on the stale worker's own clock and
/// on it observing the token before its next write.
/// </remarks>
public interface IExecutionFence
{
    /// <summary>
    /// The lease token in scope, or <see langword="null"/> when the caller holds no lease — the
    /// synchronous execution path, in which no other worker can be processing the execution.
    /// </summary>
    Guid? CurrentToken { get; }

    IDisposable Hold(Guid leaseToken);
}

public interface IExecutionStore
{
    Task<ExecutionSubmission?> FindByIdempotencyKeyAsync(
        string tenantId,
        string key,
        CancellationToken cancellationToken);
    Task<ExecutionSnapshot?> GetAsync(Guid executionId, CancellationToken cancellationToken);

    /// <summary>
    /// Inserts a new execution.
    /// </summary>
    /// <exception cref="IdempotencyConflictException">
    /// The tenant already has an execution for <paramref name="idempotencyKey"/>. Raised instead of
    /// a provider-specific unique-violation so concurrent submits can be resolved by re-reading.
    /// </exception>
    Task CreateAsync(
        ExecutionSnapshot execution,
        string? idempotencyKey,
        string? inputFingerprint,
        CancellationToken cancellationToken);
    /// <summary>
    /// Persists the mutable execution state. <see cref="ExecutionSnapshot.CancellationRequestedAt"/>
    /// is owned by the store and is never written from the supplied snapshot, so a transition
    /// computed before a cancellation arrived cannot erase it.
    /// </summary>
    Task UpdateAsync(ExecutionSnapshot execution, CancellationToken cancellationToken);

    /// <summary>
    /// Atomically transitions an execution only when its persisted status still matches
    /// <paramref name="expectedStatus"/>. This is the durable claim used by resumable inline
    /// workflows, where more than one caller may replay the same command concurrently.
    /// </summary>
    /// <returns>The updated snapshot, or <see langword="null"/> when the status no longer matches.</returns>
    Task<ExecutionSnapshot?> TryTransitionAsync(
        Guid executionId,
        ExecutionStatus expectedStatus,
        ExecutionStatus targetStatus,
        DateTimeOffset updatedAt,
        CancellationToken cancellationToken);

    /// <summary>
    /// Records a cancellation request without touching any other column.
    /// </summary>
    /// <returns>
    /// <see langword="false"/> when the execution is missing or already terminal, in which case
    /// nothing is written and the recorded outcome is preserved.
    /// </returns>
    Task<bool> TryRequestCancellationAsync(
        Guid executionId,
        DateTimeOffset requestedAt,
        CancellationToken cancellationToken);

    IAsyncEnumerable<ExecutionEvent> ReadEventsAsync(Guid executionId, long afterSequence, CancellationToken cancellationToken);
    Task<ExecutionEvent> AppendEventAsync(
        Guid executionId,
        string eventType,
        DateTimeOffset occurredAt,
        JsonElement data,
        CancellationToken cancellationToken);
}

public interface IExecutionWorkStore
{
    Task<ExecutionWorkItem> EnqueueAsync(
        Guid executionId,
        ExecutionStatus expectedStatus,
        DateTimeOffset queuedAt,
        CancellationToken cancellationToken);

    Task<ExecutionLease?> TryClaimAsync(
        string workerId,
        DateTimeOffset claimedAt,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken);

    Task<ExecutionLeaseHeartbeat> RenewAsync(
        ExecutionLease lease,
        DateTimeOffset renewedAt,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken);

    Task<bool> CompleteAsync(
        ExecutionLease lease,
        DateTimeOffset completedAt,
        CancellationToken cancellationToken);

    Task<bool> AbandonAsync(
        ExecutionLease lease,
        DateTimeOffset availableAt,
        CancellationToken cancellationToken);

    Task<bool> CancelPendingAsync(
        Guid executionId,
        DateTimeOffset cancelledAt,
        Problem problem,
        CancellationToken cancellationToken);

    Task<ExecutionWorkItem?> GetAsync(Guid executionId, CancellationToken cancellationToken);
}

public enum ExecutionWorkState
{
    Pending,
    Leased,
    Completed,
    Cancelled
}

public sealed record ExecutionWorkItem(
    Guid ExecutionId,
    ExecutionWorkState State,
    DateTimeOffset AvailableAt,
    int DeliveryAttempt,
    string? LeaseOwner = null,
    Guid? LeaseToken = null,
    DateTimeOffset? LeaseExpiresAt = null,
    DateTimeOffset? HeartbeatAt = null,
    DateTimeOffset? CompletedAt = null);

public sealed record ExecutionLease(
    Guid ExecutionId,
    string WorkerId,
    Guid LeaseToken,
    int DeliveryAttempt,
    DateTimeOffset ExpiresAt);

public sealed record ExecutionLeaseHeartbeat(
    bool Renewed,
    bool CancellationRequested,
    DateTimeOffset? ExpiresAt = null);
