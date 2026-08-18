using iRoute.Common;
using Microsoft.EntityFrameworkCore;

namespace iRoute.Data;

public sealed class InMemoryObservabilityStore(
    InMemoryExecutionStore executions,
    ObservabilityOptions options) : IObservabilityStore
{
    public Task<ObservabilitySummary> QueryAsync(
        ObservabilityQuery query,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ObservabilityProjection.Validate(query, options);
        var candidates = executions.ObservabilitySnapshot()
            .Where(item => ObservabilityProjection.Matches(item, query))
            .OrderByDescending(item => item.CreatedAt)
            .ThenByDescending(item => item.ExecutionId)
            .Take(options.MaxExecutions + 1)
            .ToArray();
        return Task.FromResult(ObservabilityProjection.Summarize(
            query,
            candidates.Take(options.MaxExecutions),
            candidates.Length > options.MaxExecutions,
            options.MaxRecentExecutions,
            executions.ObservabilityEvents));
    }

    public Task<ExecutionTimeline?> GetTimelineAsync(
        string tenantId,
        Guid executionId,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var snapshot = executions.ObservabilitySnapshot().SingleOrDefault(item =>
            item.ExecutionId == executionId &&
            string.Equals(item.TenantId, tenantId, StringComparison.Ordinal));
        return Task.FromResult(snapshot is null
            ? null
            : ObservabilityProjection.Timeline(
                snapshot,
                executions.ObservabilityEvents(executionId),
                options));
    }
}
