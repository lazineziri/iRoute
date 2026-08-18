using System.Data;
using iRoute.Common;
using Microsoft.EntityFrameworkCore;

namespace iRoute.Data;

public sealed class EfArtifactStore(
    IDbContextFactory<IRouteDbContext> contextFactory,
    LifecyclePolicy? lifecyclePolicy = null) : IArtifactStore
{
    private readonly LifecyclePolicy _lifecyclePolicy = lifecyclePolicy ?? new LifecyclePolicy();

    public async Task<ArtifactRecord?> FindReusableAsync(
        ArtifactReuseQuery query,
        CancellationToken cancellationToken)
    {
        var projectId = query.ProjectId ?? string.Empty;
        var now = query.At.ToUnixTimeMilliseconds();
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var entity = await context.Artifacts
            .AsNoTracking()
            .Where(x =>
                x.TenantId == query.TenantId &&
                x.ProjectId == projectId &&
                x.TaskType == query.TaskType &&
                x.TaskDefinitionVersion == query.TaskDefinitionVersion &&
                x.InputHash == query.InputHash &&
                x.LogicalKey == query.LogicalKey &&
                x.IsActive &&
                x.LifecycleStatus == ArtifactLifecycleStatus.Active &&
                (x.ExpiresAtUnixMilliseconds == null || x.ExpiresAtUnixMilliseconds > now))
            .OrderByDescending(x => x.Version)
            .FirstOrDefaultAsync(cancellationToken);
        return entity is null ? null : await ToRecordAsync(context, entity, cancellationToken);
    }

    public async Task<ArtifactRecord?> GetAsync(
        string tenantId,
        Guid artifactId,
        CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var entity = await context.Artifacts
            .AsNoTracking()
            .SingleOrDefaultAsync(
                x => x.TenantId == tenantId && x.ArtifactId == artifactId,
                cancellationToken);
        return entity is null ? null : await ToRecordAsync(context, entity, cancellationToken);
    }

    public async Task<ArtifactRecord?> FindActiveAsync(
        ArtifactLookupQuery query,
        CancellationToken cancellationToken)
    {
        var projectId = query.ProjectId ?? string.Empty;
        var now = query.At.ToUnixTimeMilliseconds();
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var entity = await context.Artifacts
            .AsNoTracking()
            .Where(item =>
                item.TenantId == query.TenantId &&
                item.ProjectId == projectId &&
                item.TaskType == query.TaskType &&
                item.TaskDefinitionVersion == query.TaskDefinitionVersion &&
                item.ArtifactType == query.ArtifactType &&
                item.LogicalKey == query.LogicalKey &&
                item.IsActive &&
                item.LifecycleStatus == ArtifactLifecycleStatus.Active &&
                (item.ExpiresAtUnixMilliseconds == null || item.ExpiresAtUnixMilliseconds > now))
            .OrderByDescending(item => item.Version)
            .ThenByDescending(item => item.ArtifactId)
            .FirstOrDefaultAsync(cancellationToken);
        return entity is null ? null : await ToRecordAsync(context, entity, cancellationToken);
    }

    public Task<ArtifactRecord> SaveAsync(
        ArtifactRecord artifact,
        CancellationToken cancellationToken) =>
        PersistenceContention.RetryAsync(
            () => SaveCoreAsync(artifact, cancellationToken),
            cancellationToken);

    private async Task<ArtifactRecord> SaveCoreAsync(
        ArtifactRecord artifact,
        CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await context.Database.BeginTransactionAsync(
            IsolationLevel.Serializable,
            cancellationToken);
        var projectId = artifact.ProjectId ?? string.Empty;
        var logicalKey = artifact.EffectiveLogicalKey;
        var lineage = await context.Artifacts
            .Where(x =>
                x.TenantId == artifact.TenantId &&
                x.ProjectId == projectId &&
                x.ArtifactType == artifact.ArtifactType &&
                x.LogicalKey == logicalKey)
            .OrderByDescending(x => x.Version)
            .ThenByDescending(x => x.ArtifactId)
            .ToListAsync(cancellationToken);
        var active = lineage.FirstOrDefault(item =>
            item.IsActive && item.LifecycleStatus == ArtifactLifecycleStatus.Active);
        if (active is not null &&
            string.Equals(active.InputHash, artifact.InputHash, StringComparison.Ordinal) &&
            string.Equals(active.ContentHash, artifact.ContentHash, StringComparison.Ordinal) &&
            (active.ExpiresAtUnixMilliseconds is null ||
                active.ExpiresAtUnixMilliseconds > artifact.CreatedAt.ToUnixTimeMilliseconds()))
        {
            var existing = await ToRecordAsync(context, active, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return existing;
        }

        var previous = lineage.FirstOrDefault();
        var versioned = artifact with
        {
            Version = checked((previous?.Version ?? 0) + 1),
            LogicalKey = logicalKey,
            IsActive = true,
            LifecycleStatus = ArtifactLifecycleStatus.Active,
            SupersedesArtifactId = previous?.ArtifactId,
            SupersededByArtifactId = null,
            Dependencies = EfMemoryStore.NormalizeDependencies(artifact.EffectiveDependencies),
            InvalidatedAt = null,
            InvalidationReason = null,
            ExpiresAt = artifact.ExpiresAt ??
                artifact.CreatedAt.Add(_lifecyclePolicy.DefaultArtifactTimeToLive)
        };
        if (active is not null)
        {
            active.IsActive = false;
            active.LifecycleStatus = ArtifactLifecycleStatus.Superseded;
            active.SupersededByArtifactId = versioned.ArtifactId;
        }

        context.Artifacts.Add(PersistenceMapping.ToEntity(versioned));
        EfMemoryStore.AddDependencyEdges(
            context,
            "artifact",
            versioned.ArtifactId,
            versioned.TenantId,
            versioned.EffectiveDependencies);
        await context.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return versioned;
    }

    public Task<ArtifactInvalidationResult> InvalidateByDependencyAsync(
        DependencyChange change,
        CancellationToken cancellationToken) =>
        PersistenceContention.RetryAsync(
            () => InvalidateByDependencyCoreAsync(change, cancellationToken),
            cancellationToken);

    private async Task<ArtifactInvalidationResult> InvalidateByDependencyCoreAsync(
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
            var sourceIds = await EfMemoryStore.MatchingSourceIdsAsync(
                context,
                current,
                "artifact",
                cancellationToken);
            var candidates = await context.Artifacts
                .Where(item =>
                    item.TenantId == change.TenantId &&
                    sourceIds.Contains(item.ArtifactId) &&
                    item.IsActive &&
                    item.LifecycleStatus == ArtifactLifecycleStatus.Active)
                .OrderBy(item => item.Version)
                .ThenBy(item => item.ArtifactId)
                .ToListAsync(cancellationToken);
            foreach (var candidate in candidates.Where(item => seen.Add(item.ArtifactId)))
            {
                candidate.IsActive = false;
                candidate.LifecycleStatus = ArtifactLifecycleStatus.Invalidated;
                candidate.InvalidatedAtUnixMilliseconds = change.OccurredAt.ToUnixTimeMilliseconds();
                candidate.InvalidationReason = change.Reason;
                invalidated.Add(candidate.ArtifactId);
                changes.Enqueue(new DependencyChange(
                    change.TenantId,
                    "artifact",
                    candidate.ArtifactId.ToString(),
                    candidate.ContentHash,
                    true,
                    change.Reason,
                    change.OccurredAt));
            }
        }

        await context.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new ArtifactInvalidationResult(invalidated);
    }

    private static async Task<ArtifactRecord> ToRecordAsync(
        IRouteDbContext context,
        ArtifactEntity entity,
        CancellationToken cancellationToken) =>
        PersistenceMapping.ToContract(
            entity,
            await EfMemoryStore.ReadDependenciesAsync(
                context,
                "artifact",
                entity.ArtifactId,
                cancellationToken));
}
