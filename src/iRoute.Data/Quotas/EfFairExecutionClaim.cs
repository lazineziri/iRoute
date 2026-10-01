using System.Data;
using iRoute.Common;
using Microsoft.EntityFrameworkCore;

namespace iRoute.Data;

internal sealed class EfFairExecutionClaim(IDbContextFactory<IRouteDbContext> contexts, TenantQuotaOptions options)
{
    public Task<ExecutionLease?> TryClaimAsync(string worker, DateTimeOffset at, TimeSpan duration, CancellationToken token) =>
        PersistenceContention.RetryAsync(() => ClaimCoreAsync(worker, at, duration, token), token);

    private async Task<ExecutionLease?> ClaimCoreAsync(string worker, DateTimeOffset at, TimeSpan duration, CancellationToken token)
    {
        await using var context = await contexts.CreateDbContextAsync(token);
        await using var transaction = await context.Database.BeginTransactionAsync(IsolationLevel.Serializable, token);
        var now = at.ToUnixTimeMilliseconds();
        var eligible = from work in context.ExecutionWorkItems
                       join execution in context.Executions on work.ExecutionId equals execution.ExecutionId
                       where (work.State == ExecutionWorkState.Pending && work.AvailableAtUnixMilliseconds <= now) ||
                             (work.State == ExecutionWorkState.Leased && work.LeaseExpiresAtUnixMilliseconds <= now)
                       group work by execution.TenantId into tenant
                       select new { TenantId = tenant.Key, AvailableAt = tenant.Min(item => item.AvailableAtUnixMilliseconds) };
        var tenants = await eligible.ToArrayAsync(token);
        if (tenants.Length == 0) return null;
        var ids = tenants.Select(item => item.TenantId).ToArray();
        var dispatch = await context.TenantDispatch.Where(item => ids.Contains(item.TenantId)).ToDictionaryAsync(item => item.TenantId, token);
        var activeQuery = from work in context.ExecutionWorkItems
                          join execution in context.Executions on work.ExecutionId equals execution.ExecutionId
                          where work.State == ExecutionWorkState.Leased && work.LeaseExpiresAtUnixMilliseconds > now
                          group work by execution.TenantId into tenant
                          select new { TenantId = tenant.Key, Count = tenant.Count() };
        var active = await activeQuery.ToDictionaryAsync(item => item.TenantId, item => item.Count, token);
        var selected = tenants.Where(item => !options.Enabled ||
                active.GetValueOrDefault(item.TenantId) < options.ForTenant(item.TenantId).MaxConcurrentExecutions)
            .OrderBy(item => dispatch.GetValueOrDefault(item.TenantId)?.LastDispatch ?? 0)
            .ThenBy(item => item.AvailableAt).ThenBy(item => item.TenantId, StringComparer.Ordinal).FirstOrDefault();
        if (selected is null) return null;
        var candidate = await (from work in context.ExecutionWorkItems.AsNoTracking()
                               join execution in context.Executions on work.ExecutionId equals execution.ExecutionId
                               where execution.TenantId == selected.TenantId &&
                                   ((work.State == ExecutionWorkState.Pending && work.AvailableAtUnixMilliseconds <= now) ||
                                    (work.State == ExecutionWorkState.Leased && work.LeaseExpiresAtUnixMilliseconds <= now))
                               orderby work.AvailableAtUnixMilliseconds, work.ExecutionId
                               select work).FirstAsync(token);
        var leaseToken = Guid.CreateVersion7();
        var expiresAt = at.Add(duration);
        await context.ExecutionWorkItems.Where(item => item.ExecutionId == candidate.ExecutionId).ExecuteUpdateAsync(
            setters => setters.SetProperty(item => item.State, ExecutionWorkState.Leased)
                .SetProperty(item => item.DeliveryAttempt, item => item.DeliveryAttempt + 1)
                .SetProperty(item => item.LeaseOwner, worker).SetProperty(item => item.LeaseToken, leaseToken)
                .SetProperty(item => item.LeaseExpiresAtUnixMilliseconds, expiresAt.ToUnixTimeMilliseconds())
                .SetProperty(item => item.HeartbeatAtUnixMilliseconds, now)
                .SetProperty(item => item.CompletedAtUnixMilliseconds, (long?)null), token);
        var lastDispatch = await context.TenantDispatch.MaxAsync(item => (long?)item.LastDispatch, token) ?? 0;
        if (!dispatch.TryGetValue(selected.TenantId, out var tenantDispatch))
        {
            tenantDispatch = new() { TenantId = selected.TenantId };
            context.TenantDispatch.Add(tenantDispatch);
        }
        tenantDispatch.LastDispatch = checked(lastDispatch + 1);
        await context.SaveChangesAsync(token);
        await transaction.CommitAsync(token);
        return new(candidate.ExecutionId, worker, leaseToken, checked(candidate.DeliveryAttempt + 1), expiresAt);
    }
}
