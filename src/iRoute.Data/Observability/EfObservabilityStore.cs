using iRoute.Common;
using Microsoft.EntityFrameworkCore;

namespace iRoute.Data;

public sealed class EfObservabilityStore(
    IDbContextFactory<IRouteDbContext> contextFactory,
    ObservabilityOptions options) : IObservabilityStore
{
    public async Task<ObservabilitySummary> QueryAsync(
        ObservabilityQuery query,
        CancellationToken cancellationToken)
    {
        ObservabilityProjection.Validate(query, options);
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var from = query.From.ToUnixTimeMilliseconds();
        var to = query.To.ToUnixTimeMilliseconds();
        var executionQuery = context.Executions.AsNoTracking().Where(item =>
            item.TenantId == query.TenantId &&
            item.CreatedAtUnixMilliseconds >= from &&
            item.CreatedAtUnixMilliseconds <= to);
        if (!string.IsNullOrWhiteSpace(query.TaskType))
        {
            executionQuery = executionQuery.Where(item => item.TaskType == query.TaskType);
        }

        var entities = await executionQuery
            .OrderByDescending(item => item.CreatedAtUnixMilliseconds)
            .ThenByDescending(item => item.ExecutionId)
            .Take(options.MaxExecutions + 1)
            .ToArrayAsync(cancellationToken);
        var snapshots = entities
            .Take(options.MaxExecutions)
            .Select(PersistenceMapping.ToContract)
            .ToArray();
        var ids = snapshots.Select(item => item.ExecutionId).ToArray();
        ExecutionEventEntity[] events = ids.Length == 0
            ? []
            : await context.ExecutionEvents
                .AsNoTracking()
                .Where(item => ids.Contains(item.ExecutionId))
                .OrderBy(item => item.ExecutionId)
                .ThenBy(item => item.Sequence)
                .ToArrayAsync(cancellationToken);
        var eventsByExecution = events
            .Select(PersistenceMapping.ToContract)
            .GroupBy(item => item.ExecutionId)
            .ToDictionary(group => group.Key, group => (IReadOnlyList<ExecutionEvent>)group.ToArray());
        return ObservabilityProjection.Summarize(
            query,
            snapshots,
            entities.Length > options.MaxExecutions,
            options.MaxRecentExecutions,
            id => eventsByExecution.GetValueOrDefault(id) ?? []);
    }

    public async Task<ExecutionTimeline?> GetTimelineAsync(
        string tenantId,
        Guid executionId,
        CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var entity = await context.Executions.AsNoTracking().SingleOrDefaultAsync(item =>
            item.TenantId == tenantId && item.ExecutionId == executionId,
            cancellationToken);
        if (entity is null)
        {
            return null;
        }

        var events = await context.ExecutionEvents
            .AsNoTracking()
            .Where(item => item.ExecutionId == executionId)
            .OrderBy(item => item.Sequence)
            .Take(options.MaxTimelineEvents + 1)
            .ToArrayAsync(cancellationToken);
        return ObservabilityProjection.Timeline(
            PersistenceMapping.ToContract(entity),
            events.Select(PersistenceMapping.ToContract).ToArray(),
            options);
    }
}
