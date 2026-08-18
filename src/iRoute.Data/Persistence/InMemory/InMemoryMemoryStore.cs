using System.Collections.Concurrent;
using iRoute.Common;

namespace iRoute.Data;

public sealed class InMemoryMemoryStore(LifecyclePolicy? lifecyclePolicy = null) : IMemoryStore
{
    private readonly ConcurrentDictionary<Guid, MemoryRecord> _records = new();
    private readonly object _sync = new();
    private readonly LifecyclePolicy _lifecyclePolicy = lifecyclePolicy ?? new LifecyclePolicy();

    public Task<MemoryWriteResult> UpsertAsync(
        MemoryRecord record,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_sync)
        {
            if (_records.ContainsKey(record.MemoryId))
            {
                throw new InvalidOperationException("Memory record already exists.");
            }

            var lineage = _records.Values
                .Where(item => SameLineage(item, record))
                .OrderByDescending(item => item.Version)
                .ThenByDescending(item => item.MemoryId)
                .ToArray();
            var active = lineage.FirstOrDefault(item => item.LifecycleStatus == MemoryLifecycleStatus.Active);
            if (active is not null &&
                string.Equals(active.ContentHash, record.ContentHash, StringComparison.Ordinal) &&
                (active.ExpiresAt is null || active.ExpiresAt > record.CreatedAt))
            {
                return Task.FromResult(new MemoryWriteResult(active, null, false));
            }

            var previous = lineage.FirstOrDefault();
            var versioned = record with
            {
                Version = checked((previous?.Version ?? 0) + 1),
                Value = record.Value.Clone(),
                LifecycleStatus = MemoryLifecycleStatus.Active,
                SupersedesMemoryId = previous?.MemoryId,
                SupersededByMemoryId = null,
                InvalidatedAt = null,
                InvalidationReason = null,
                ExpiresAt = record.ExpiresAt ??
                    record.CreatedAt.Add(_lifecyclePolicy.DefaultMemoryTimeToLive),
                Dependencies = record.Dependencies
                    .DistinctBy(item => (item.Kind, item.Reference))
                    .OrderBy(item => item.Kind, StringComparer.Ordinal)
                    .ThenBy(item => item.Reference, StringComparer.Ordinal)
                    .ToArray()
            };
            if (active is not null)
            {
                _records[active.MemoryId] = active with
                {
                    LifecycleStatus = MemoryLifecycleStatus.Superseded,
                    SupersededByMemoryId = versioned.MemoryId
                };
            }

            _records[versioned.MemoryId] = versioned;
            return Task.FromResult(new MemoryWriteResult(
                versioned,
                previous is null ? null : _records[previous.MemoryId],
                true));
        }
    }

    internal IReadOnlyList<MemoryRecord> LifecycleSnapshot()
    {
        lock (_sync)
        {
            return _records.Values.ToArray();
        }
    }

    internal bool LifecycleUpdate(MemoryRecord record)
    {
        lock (_sync)
        {
            if (!_records.ContainsKey(record.MemoryId))
            {
                return false;
            }

            _records[record.MemoryId] = record;
            return true;
        }
    }

    internal bool LifecycleRemove(Guid memoryId)
    {
        lock (_sync)
        {
            return _records.TryRemove(memoryId, out _);
        }
    }

    public Task<MemoryRecord?> GetActiveAsync(
        MemoryLookup query,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_sync)
        {
            var record = _records.Values
                .Where(item =>
                    string.Equals(item.TenantId, query.TenantId, StringComparison.Ordinal) &&
                    string.Equals(item.ProjectId ?? string.Empty, query.ProjectId ?? string.Empty, StringComparison.Ordinal) &&
                    item.Kind == query.Kind &&
                    string.Equals(item.Key, query.Key, StringComparison.Ordinal) &&
                    item.LifecycleStatus == MemoryLifecycleStatus.Active &&
                    (item.ExpiresAt is null || item.ExpiresAt > query.At))
                .OrderByDescending(item => item.Version)
                .ThenByDescending(item => item.MemoryId)
                .FirstOrDefault();
            return Task.FromResult<MemoryRecord?>(record);
        }
    }

    public Task<IReadOnlyList<MemoryRecord>> ListActiveAsync(
        ActiveMemoryQuery query,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_sync)
        {
            IReadOnlyList<MemoryRecord> records = _records.Values
                .Where(item =>
                    string.Equals(item.TenantId, query.TenantId, StringComparison.Ordinal) &&
                    string.Equals(item.ProjectId ?? string.Empty, query.ProjectId ?? string.Empty, StringComparison.Ordinal) &&
                    item.LifecycleStatus == MemoryLifecycleStatus.Active &&
                    (item.ExpiresAt is null || item.ExpiresAt > query.At))
                .OrderBy(item => item.Kind)
                .ThenBy(item => item.Key, StringComparer.Ordinal)
                .ThenByDescending(item => item.Version)
                .ThenBy(item => item.MemoryId)
                .ToArray();
            return Task.FromResult(records);
        }
    }

    public Task<MemoryRecord?> GetAsync(
        string tenantId,
        Guid memoryId,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_sync)
        {
            var record = _records.GetValueOrDefault(memoryId);
            return Task.FromResult<MemoryRecord?>(
                record is not null && string.Equals(record.TenantId, tenantId, StringComparison.Ordinal)
                    ? record
                    : null);
        }
    }

    public Task<MemoryInvalidationResult> InvalidateByDependencyAsync(
        DependencyChange change,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_sync)
        {
            var invalidated = new List<Guid>();
            var seen = new HashSet<Guid>();
            var changes = new Queue<(string Kind, string Reference, string? Hash, bool Removed)>();
            changes.Enqueue((change.Kind, change.Reference, change.CurrentContentHash, change.SourceRemoved));
            while (changes.TryDequeue(out var current))
            {
                var candidates = _records.Values
                    .Where(item =>
                        string.Equals(item.TenantId, change.TenantId, StringComparison.Ordinal) &&
                        item.LifecycleStatus == MemoryLifecycleStatus.Active &&
                        item.Dependencies.Any(dependency => Matches(dependency, current)))
                    .OrderBy(item => item.Version)
                    .ThenBy(item => item.MemoryId)
                    .ToArray();
                foreach (var record in candidates.Where(item => seen.Add(item.MemoryId)))
                {
                    _records[record.MemoryId] = record with
                    {
                        LifecycleStatus = MemoryLifecycleStatus.Invalidated,
                        InvalidatedAt = change.OccurredAt,
                        InvalidationReason = change.Reason
                    };
                    invalidated.Add(record.MemoryId);
                    changes.Enqueue(("memory", record.MemoryId.ToString(), record.ContentHash, true));
                }
            }

            return Task.FromResult(new MemoryInvalidationResult(invalidated));
        }
    }

    private static bool Matches(
        DependencyReference dependency,
        (string Kind, string Reference, string? Hash, bool Removed) change) =>
        string.Equals(dependency.Kind, change.Kind, StringComparison.Ordinal) &&
        string.Equals(dependency.Reference, change.Reference, StringComparison.Ordinal) &&
        (change.Removed ||
            change.Hash is null ||
            !string.Equals(dependency.ContentHash, change.Hash, StringComparison.Ordinal));

    private static bool SameLineage(MemoryRecord existing, MemoryRecord proposed) =>
        string.Equals(existing.TenantId, proposed.TenantId, StringComparison.Ordinal) &&
        string.Equals(existing.ProjectId ?? string.Empty, proposed.ProjectId ?? string.Empty, StringComparison.Ordinal) &&
        existing.Kind == proposed.Kind &&
        string.Equals(existing.Key, proposed.Key, StringComparison.Ordinal);
}
