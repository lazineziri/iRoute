using System.Data;
using System.Text.Json;
using iRoute.Contracts;
using iRoute.Core;
using Microsoft.EntityFrameworkCore;

namespace iRoute.Infrastructure;

public sealed class MemoryEntity
{
    public Guid MemoryId { get; set; }
    public string TenantId { get; set; } = null!;
    public string ProjectId { get; set; } = string.Empty;
    public MemoryKind Kind { get; set; }
    public string Key { get; set; } = null!;
    public int Version { get; set; }
    public string ValueJson { get; set; } = null!;
    public string ContentHash { get; set; } = null!;
    public MemoryLifecycleStatus LifecycleStatus { get; set; }
    public string EvidenceJson { get; set; } = null!;
    public long CreatedAtUnixMilliseconds { get; set; }
    public long? ExpiresAtUnixMilliseconds { get; set; }
    public Guid? SupersedesMemoryId { get; set; }
    public Guid? SupersededByMemoryId { get; set; }
    public long? InvalidatedAtUnixMilliseconds { get; set; }
    public string? InvalidationReason { get; set; }
}

public sealed class DependencyEdgeEntity
{
    public string TenantId { get; set; } = null!;
    public string SourceKind { get; set; } = null!;
    public Guid SourceId { get; set; }
    public string TargetKind { get; set; } = null!;
    public string TargetReference { get; set; } = null!;
    public string? TargetContentHash { get; set; }
}

public sealed class LifecycleArchiveEntity
{
    public LifecycleResourceKind ResourceKind { get; set; }
    public Guid ResourceId { get; set; }
    public string TenantId { get; set; } = null!;
    public string ContentHash { get; set; } = null!;
    public string PayloadJson { get; set; } = null!;
    public long ArchivedAtUnixMilliseconds { get; set; }
}

public sealed class EfMemoryStore(
    IDbContextFactory<IRouteDbContext> contextFactory,
    LifecyclePolicy? lifecyclePolicy = null) : IMemoryStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly LifecyclePolicy _lifecyclePolicy = lifecyclePolicy ?? new LifecyclePolicy();

    public Task<MemoryWriteResult> UpsertAsync(
        MemoryRecord record,
        CancellationToken cancellationToken) =>
        PersistenceContention.RetryAsync(
            () => UpsertCoreAsync(record, cancellationToken),
            cancellationToken);

    private async Task<MemoryWriteResult> UpsertCoreAsync(
        MemoryRecord record,
        CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await context.Database.BeginTransactionAsync(
            IsolationLevel.Serializable,
            cancellationToken);
        var projectId = record.ProjectId ?? string.Empty;
        var lineage = await context.MemoryRecords
            .Where(item =>
                item.TenantId == record.TenantId &&
                item.ProjectId == projectId &&
                item.Kind == record.Kind &&
                item.Key == record.Key)
            .OrderByDescending(item => item.Version)
            .ThenByDescending(item => item.MemoryId)
            .ToListAsync(cancellationToken);
        var active = lineage.FirstOrDefault(item => item.LifecycleStatus == MemoryLifecycleStatus.Active);
        if (active is not null &&
            string.Equals(active.ContentHash, record.ContentHash, StringComparison.Ordinal) &&
            (active.ExpiresAtUnixMilliseconds is null ||
                active.ExpiresAtUnixMilliseconds > record.CreatedAt.ToUnixTimeMilliseconds()))
        {
            var existing = await ToRecordAsync(context, active, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return new MemoryWriteResult(existing, null, false);
        }

        var previousEntity = lineage.FirstOrDefault();
        var versioned = record with
        {
            Version = checked((previousEntity?.Version ?? 0) + 1),
            LifecycleStatus = MemoryLifecycleStatus.Active,
            SupersedesMemoryId = previousEntity?.MemoryId,
            SupersededByMemoryId = null,
            InvalidatedAt = null,
            InvalidationReason = null,
            ExpiresAt = record.ExpiresAt ??
                record.CreatedAt.Add(_lifecyclePolicy.DefaultMemoryTimeToLive),
            Dependencies = NormalizeDependencies(record.Dependencies)
        };
        if (active is not null)
        {
            active.LifecycleStatus = MemoryLifecycleStatus.Superseded;
            active.SupersededByMemoryId = versioned.MemoryId;
        }

        context.MemoryRecords.Add(ToEntity(versioned));
        AddDependencyEdges(context, "memory", versioned.MemoryId, versioned.TenantId, versioned.Dependencies);
        await context.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        var previous = previousEntity is null
            ? null
            : await ToRecordAsync(context, previousEntity, cancellationToken);
        return new MemoryWriteResult(versioned, previous, true);
    }

    public async Task<MemoryRecord?> GetActiveAsync(
        MemoryLookup query,
        CancellationToken cancellationToken)
    {
        var projectId = query.ProjectId ?? string.Empty;
        var at = query.At.ToUnixTimeMilliseconds();
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var entity = await context.MemoryRecords
            .AsNoTracking()
            .Where(item =>
                item.TenantId == query.TenantId &&
                item.ProjectId == projectId &&
                item.Kind == query.Kind &&
                item.Key == query.Key &&
                item.LifecycleStatus == MemoryLifecycleStatus.Active &&
                (item.ExpiresAtUnixMilliseconds == null || item.ExpiresAtUnixMilliseconds > at))
            .OrderByDescending(item => item.Version)
            .ThenByDescending(item => item.MemoryId)
            .FirstOrDefaultAsync(cancellationToken);
        return entity is null ? null : await ToRecordAsync(context, entity, cancellationToken);
    }

    public async Task<IReadOnlyList<MemoryRecord>> ListActiveAsync(
        ActiveMemoryQuery query,
        CancellationToken cancellationToken)
    {
        var projectId = query.ProjectId ?? string.Empty;
        var at = query.At.ToUnixTimeMilliseconds();
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var entities = await context.MemoryRecords
            .AsNoTracking()
            .Where(item =>
                item.TenantId == query.TenantId &&
                item.ProjectId == projectId &&
                item.LifecycleStatus == MemoryLifecycleStatus.Active &&
                (item.ExpiresAtUnixMilliseconds == null || item.ExpiresAtUnixMilliseconds > at))
            .OrderBy(item => item.Kind)
            .ThenBy(item => item.Key)
            .ThenByDescending(item => item.Version)
            .ThenBy(item => item.MemoryId)
            .ToArrayAsync(cancellationToken);
        var records = new List<MemoryRecord>(entities.Length);
        foreach (var entity in entities)
        {
            records.Add(await ToRecordAsync(context, entity, cancellationToken));
        }

        return records;
    }

    public async Task<MemoryRecord?> GetAsync(
        string tenantId,
        Guid memoryId,
        CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var entity = await context.MemoryRecords
            .AsNoTracking()
            .SingleOrDefaultAsync(
                item => item.TenantId == tenantId && item.MemoryId == memoryId,
                cancellationToken);
        return entity is null ? null : await ToRecordAsync(context, entity, cancellationToken);
    }

    public Task<MemoryInvalidationResult> InvalidateByDependencyAsync(
        DependencyChange change,
        CancellationToken cancellationToken) =>
        PersistenceContention.RetryAsync(
            () => InvalidateByDependencyCoreAsync(change, cancellationToken),
            cancellationToken);

    private async Task<MemoryInvalidationResult> InvalidateByDependencyCoreAsync(
        DependencyChange change,
        CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await context.Database.BeginTransactionAsync(
            IsolationLevel.Serializable,
            cancellationToken);
        var invalidated = new List<Guid>();
        var seen = new HashSet<Guid>();
        var changes = new Queue<DependencyChange>();
        changes.Enqueue(change);
        while (changes.TryDequeue(out var current))
        {
            var sourceIds = await MatchingSourceIdsAsync(
                context,
                current,
                "memory",
                cancellationToken);
            var records = await context.MemoryRecords
                .Where(item =>
                    item.TenantId == change.TenantId &&
                    sourceIds.Contains(item.MemoryId) &&
                    item.LifecycleStatus == MemoryLifecycleStatus.Active)
                .OrderBy(item => item.Version)
                .ThenBy(item => item.MemoryId)
                .ToListAsync(cancellationToken);
            foreach (var record in records.Where(item => seen.Add(item.MemoryId)))
            {
                record.LifecycleStatus = MemoryLifecycleStatus.Invalidated;
                record.InvalidatedAtUnixMilliseconds = change.OccurredAt.ToUnixTimeMilliseconds();
                record.InvalidationReason = change.Reason;
                invalidated.Add(record.MemoryId);
                changes.Enqueue(new DependencyChange(
                    change.TenantId,
                    "memory",
                    record.MemoryId.ToString(),
                    record.ContentHash,
                    true,
                    change.Reason,
                    change.OccurredAt));
            }
        }

        await context.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new MemoryInvalidationResult(invalidated);
    }

    internal static async Task<Guid[]> MatchingSourceIdsAsync(
        IRouteDbContext context,
        DependencyChange change,
        string sourceKind,
        CancellationToken cancellationToken) =>
        await context.DependencyEdges
            .AsNoTracking()
            .Where(edge =>
                edge.TenantId == change.TenantId &&
                edge.SourceKind == sourceKind &&
                edge.TargetKind == change.Kind &&
                edge.TargetReference == change.Reference &&
                (change.SourceRemoved ||
                    change.CurrentContentHash == null ||
                    edge.TargetContentHash != change.CurrentContentHash))
            .Select(edge => edge.SourceId)
            .Distinct()
            .ToArrayAsync(cancellationToken);

    internal static void AddDependencyEdges(
        IRouteDbContext context,
        string sourceKind,
        Guid sourceId,
        string tenantId,
        IReadOnlyList<DependencyReference> dependencies)
    {
        foreach (var dependency in NormalizeDependencies(dependencies))
        {
            context.DependencyEdges.Add(new DependencyEdgeEntity
            {
                TenantId = tenantId,
                SourceKind = sourceKind,
                SourceId = sourceId,
                TargetKind = dependency.Kind,
                TargetReference = dependency.Reference,
                TargetContentHash = dependency.ContentHash
            });
        }
    }

    internal static DependencyReference[] NormalizeDependencies(
        IEnumerable<DependencyReference> dependencies) =>
        dependencies
            .DistinctBy(item => (item.Kind, item.Reference))
            .OrderBy(item => item.Kind, StringComparer.Ordinal)
            .ThenBy(item => item.Reference, StringComparer.Ordinal)
            .ToArray();

    internal static async Task<DependencyReference[]> ReadDependenciesAsync(
        IRouteDbContext context,
        string sourceKind,
        Guid sourceId,
        CancellationToken cancellationToken) =>
        await context.DependencyEdges
            .AsNoTracking()
            .Where(item => item.SourceKind == sourceKind && item.SourceId == sourceId)
            .OrderBy(item => item.TargetKind)
            .ThenBy(item => item.TargetReference)
            .Select(item => new DependencyReference(
                item.TargetKind,
                item.TargetReference,
                item.TargetContentHash))
            .ToArrayAsync(cancellationToken);

    private static MemoryEntity ToEntity(MemoryRecord record) => new()
    {
        MemoryId = record.MemoryId,
        TenantId = record.TenantId,
        ProjectId = record.ProjectId ?? string.Empty,
        Kind = record.Kind,
        Key = record.Key,
        Version = record.Version,
        ValueJson = record.Value.GetRawText(),
        ContentHash = record.ContentHash,
        LifecycleStatus = record.LifecycleStatus,
        EvidenceJson = JsonSerializer.Serialize(record.Evidence, JsonOptions),
        CreatedAtUnixMilliseconds = record.CreatedAt.ToUnixTimeMilliseconds(),
        ExpiresAtUnixMilliseconds = record.ExpiresAt?.ToUnixTimeMilliseconds(),
        SupersedesMemoryId = record.SupersedesMemoryId,
        SupersededByMemoryId = record.SupersededByMemoryId,
        InvalidatedAtUnixMilliseconds = record.InvalidatedAt?.ToUnixTimeMilliseconds(),
        InvalidationReason = record.InvalidationReason
    };

    private static async Task<MemoryRecord> ToRecordAsync(
        IRouteDbContext context,
        MemoryEntity entity,
        CancellationToken cancellationToken) => new(
        entity.MemoryId,
        entity.TenantId,
        string.IsNullOrEmpty(entity.ProjectId) ? null : entity.ProjectId,
        entity.Kind,
        entity.Key,
        entity.Version,
        JsonSerializer.Deserialize<JsonElement>(entity.ValueJson, JsonOptions),
        entity.ContentHash,
        entity.LifecycleStatus,
        JsonSerializer.Deserialize<EvidenceReference[]>(entity.EvidenceJson, JsonOptions) ?? [],
        await ReadDependenciesAsync(context, "memory", entity.MemoryId, cancellationToken),
        DateTimeOffset.FromUnixTimeMilliseconds(entity.CreatedAtUnixMilliseconds),
        entity.ExpiresAtUnixMilliseconds is { } expiresAt
            ? DateTimeOffset.FromUnixTimeMilliseconds(expiresAt)
            : null,
        entity.SupersedesMemoryId,
        entity.SupersededByMemoryId,
        entity.InvalidatedAtUnixMilliseconds is { } invalidatedAt
            ? DateTimeOffset.FromUnixTimeMilliseconds(invalidatedAt)
            : null,
        entity.InvalidationReason);
}
