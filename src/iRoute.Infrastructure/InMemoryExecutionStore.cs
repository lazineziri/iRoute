using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using iRoute.Contracts;
using iRoute.Core;

namespace iRoute.Infrastructure;

public sealed class InMemoryExecutionStore : IExecutionStore
{
    private readonly object _statusGate = new();
    private readonly ConcurrentDictionary<Guid, ExecutionSnapshot> _executions = new();
    private readonly ConcurrentDictionary<string, Guid> _idempotencyKeys = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<Guid, ConcurrentQueue<ExecutionEvent>> _events = new();
    private readonly ConcurrentDictionary<Guid, long> _eventSequences = new();

    public Task<ExecutionSnapshot?> FindByIdempotencyKeyAsync(
        string tenantId,
        string key,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var scopedKey = CreateIdempotencyKey(tenantId, key);
        return Task.FromResult(_idempotencyKeys.TryGetValue(scopedKey, out var id) && _executions.TryGetValue(id, out var value)
            ? value
            : null);
    }

    public Task<ExecutionSnapshot?> GetAsync(Guid executionId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(_executions.GetValueOrDefault(executionId));
    }

    public Task CreateAsync(ExecutionSnapshot execution, string? idempotencyKey, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!_executions.TryAdd(execution.ExecutionId, execution))
        {
            throw new InvalidOperationException("Execution already exists.");
        }

        if (!string.IsNullOrWhiteSpace(idempotencyKey))
        {
            var scopedKey = CreateIdempotencyKey(execution.TenantId, idempotencyKey);
            if (!_idempotencyKeys.TryAdd(scopedKey, execution.ExecutionId))
            {
                _executions.TryRemove(execution.ExecutionId, out _);
                throw new InvalidOperationException("The idempotency key already exists for this tenant.");
            }
        }

        return Task.CompletedTask;
    }

    public Task UpdateAsync(ExecutionSnapshot execution, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_statusGate)
        {
            // CancellationRequestedAt is store-owned: a transition computed before a cancellation
            // arrived must not carry the stale null back over it.
            _executions[execution.ExecutionId] =
                _executions.TryGetValue(execution.ExecutionId, out var current)
                    ? execution with { CancellationRequestedAt = current.CancellationRequestedAt }
                    : execution;
        }

        return Task.CompletedTask;
    }

    public Task<bool> TryRequestCancellationAsync(
        Guid executionId,
        DateTimeOffset requestedAt,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_statusGate)
        {
            if (!_executions.TryGetValue(executionId, out var current) ||
                ExecutionStateMachine.IsTerminal(current.Status))
            {
                return Task.FromResult(false);
            }

            if (current.CancellationRequestedAt is null)
            {
                _executions[executionId] = current with { CancellationRequestedAt = requestedAt };
            }

            return Task.FromResult(true);
        }
    }

    public async IAsyncEnumerable<ExecutionEvent> ReadEventsAsync(
        Guid executionId,
        long afterSequence,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (!_events.TryGetValue(executionId, out var queue))
        {
            yield break;
        }

        foreach (var executionEvent in queue.Where(x => x.Sequence > afterSequence))
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return executionEvent;
            await Task.Yield();
        }
    }

    public Task<ExecutionEvent> AppendEventAsync(
        Guid executionId,
        string eventType,
        DateTimeOffset occurredAt,
        System.Text.Json.JsonElement data,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var sequence = _eventSequences.AddOrUpdate(executionId, 1, static (_, current) => checked(current + 1));
        var executionEvent = new ExecutionEvent(sequence, executionId, eventType, occurredAt, data.Clone());
        _events.GetOrAdd(executionId, static _ => new ConcurrentQueue<ExecutionEvent>())
            .Enqueue(executionEvent);
        return Task.FromResult(executionEvent);
    }

    internal IReadOnlyList<ExecutionSnapshot> ObservabilitySnapshot() =>
        _executions.Values.ToArray();

    internal IReadOnlyList<ExecutionEvent> ObservabilityEvents(Guid executionId) =>
        _events.TryGetValue(executionId, out var queue) ? queue.ToArray() : [];

    internal ExecutionSnapshot Queue(Guid executionId, ExecutionStatus expectedStatus, DateTimeOffset queuedAt)
    {
        lock (_statusGate)
        {
            if (!_executions.TryGetValue(executionId, out var current))
            {
                throw new KeyNotFoundException($"Execution '{executionId}' was not found.");
            }

            if (current.Status != expectedStatus)
            {
                throw new InvalidOperationException(
                    $"Execution '{executionId}' cannot be queued from {current.Status}; expected {expectedStatus}.");
            }

            var queued = current with { Status = ExecutionStatus.Queued, UpdatedAt = queuedAt };
            _executions[executionId] = queued;
            return queued;
        }
    }

    internal bool CancelQueued(Guid executionId, DateTimeOffset cancelledAt, Problem problem)
    {
        lock (_statusGate)
        {
            if (!_executions.TryGetValue(executionId, out var current) ||
                current.Status != ExecutionStatus.Queued)
            {
                return false;
            }

            _executions[executionId] = current with
            {
                Status = ExecutionStatus.Cancelled,
                UpdatedAt = cancelledAt,
                CancellationRequestedAt = current.CancellationRequestedAt ?? cancelledAt,
                Error = problem
            };
            return true;
        }
    }

    private static string CreateIdempotencyKey(string tenantId, string key) => $"{tenantId}\u001f{key}";
}
