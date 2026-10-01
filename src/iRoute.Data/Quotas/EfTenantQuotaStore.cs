using System.Data;
using iRoute.Common;
using Microsoft.EntityFrameworkCore;

namespace iRoute.Data;

public sealed class EfTenantQuotaStore(IDbContextFactory<IRouteDbContext> contexts) : ITenantQuotaStore
{
    public Task<TenantQuotaPermit> TryReserveAsync(TenantQuotaReservation reservation, TenantQuotaPolicy policy, CancellationToken token) =>
        PersistenceContention.RetryAsync(() => ReserveCoreAsync(reservation, policy, token), token);

    private async Task<TenantQuotaPermit> ReserveCoreAsync(TenantQuotaReservation request, TenantQuotaPolicy policy, CancellationToken token)
    {
        policy.EnsureValid();
        if (string.IsNullOrWhiteSpace(request.TenantId) || request.TenantId.Length > 200 || request.ReservedTokens < 0 ||
            request.ReservedCost is < 0 or > 1000000 || request.WindowSeconds is < 1 or > 3600 || request.LeaseExpiresAt <= request.CreatedAt)
            throw new ArgumentException("The quota reservation is invalid.", nameof(request));
        await using var context = await contexts.CreateDbContextAsync(token);
        await using var transaction = await context.Database.BeginTransactionAsync(IsolationLevel.Serializable, token);
        var existing = await context.TenantQuotaReservations.FindAsync([request.ReservationId], token);
        if (existing is not null)
        {
            if (existing.TenantId != request.TenantId) throw new InvalidOperationException("Quota reservation scope mismatch.");
            return new(true);
        }
        var now = request.CreatedAt.ToUnixTimeMilliseconds();
        var start = WindowStart(now, request.WindowSeconds);
        var end = checked(start + request.WindowSeconds * 1000L);
        await context.TenantQuotaReservations.Where(item => item.TenantId == request.TenantId &&
            item.WindowEndUnixMilliseconds <= now && item.LeaseExpiresAtUnixMilliseconds <= now).ExecuteDeleteAsync(token);
        var snapshot = await InspectCoreAsync(context, request.TenantId, now, start, token);
        string? rejection = snapshot.ActiveModelCalls >= policy.MaxConcurrentModelCalls ? "concurrency" :
            snapshot.Attempts >= policy.MaxModelAttemptsPerWindow ? "attempt-rate" :
            request.ReservedTokens > policy.MaxTokensPerWindow - snapshot.ChargedTokens ? "tokens" : null;
        if (policy.MaxCostPerWindow is { } cost)
        {
            if (request.ReservedCost is null || snapshot.ChargedCost is null) rejection ??= "unknown-cost";
            else if (request.ReservedCost > cost - snapshot.ChargedCost) rejection ??= "cost";
        }
        if (rejection is not null) return new(false, rejection, TimeSpan.FromMilliseconds(Math.Max(1, end - now)));
        var account = await context.TenantQuotaAccounts.FindAsync([request.TenantId], token);
        if (account is null)
        {
            account = new() { TenantId = request.TenantId };
            context.TenantQuotaAccounts.Add(account);
        }
        account.Revision = checked(account.Revision + 1);
        context.TenantQuotaReservations.Add(new()
        {
            ReservationId = request.ReservationId,
            TenantId = request.TenantId,
            WindowStartUnixMilliseconds = start,
            WindowEndUnixMilliseconds = end,
            LeaseExpiresAtUnixMilliseconds = request.LeaseExpiresAt.ToUnixTimeMilliseconds(),
            ChargedTokens = request.ReservedTokens,
            ChargedCostMicroUnits = MicroUnits(request.ReservedCost)
        });
        await context.SaveChangesAsync(token);
        await transaction.CommitAsync(token);
        return new(true);
    }

    public Task ReconcileAsync(Guid reservationId, UsageSummary? usage, DateTimeOffset completedAt, CancellationToken token) =>
        PersistenceContention.RetryAsync(async () =>
        {
            await using var context = await contexts.CreateDbContextAsync(token);
            await using var transaction = await context.Database.BeginTransactionAsync(IsolationLevel.Serializable, token);
            var reservation = await context.TenantQuotaReservations.FindAsync([reservationId], token);
            if (reservation is null || reservation.CompletedAtUnixMilliseconds is not null) return false;
            var account = await context.TenantQuotaAccounts.FindAsync([reservation.TenantId], token);
            account!.Revision = checked(account.Revision + 1);
            if (usage is not null)
            {
                if (usage.InputTokens < 0 || usage.OutputTokens < 0 || usage.Cost < 0)
                    throw new ArgumentException("Reported quota usage must be nonnegative.", nameof(usage));
                reservation.ChargedTokens = checked((long)usage.InputTokens + usage.OutputTokens);
                reservation.ChargedCostMicroUnits = usage.CostKnown ? MicroUnits(usage.Cost) : null;
                reservation.UsageUnknown = false;
            }
            reservation.CompletedAtUnixMilliseconds = completedAt.ToUnixTimeMilliseconds();
            reservation.LeaseExpiresAtUnixMilliseconds = completedAt.ToUnixTimeMilliseconds();
            await context.SaveChangesAsync(token);
            await transaction.CommitAsync(token);
            return true;
        }, token);

    public async Task<TenantQuotaSnapshot> InspectAsync(string tenant, DateTimeOffset at, int windowSeconds, CancellationToken token)
    {
        await using var context = await contexts.CreateDbContextAsync(token);
        var now = at.ToUnixTimeMilliseconds();
        return await InspectCoreAsync(context, tenant, now, WindowStart(now, windowSeconds), token);
    }

    private static async Task<TenantQuotaSnapshot> InspectCoreAsync(IRouteDbContext context, string tenant, long now, long start, CancellationToken token)
    {
        var window = context.TenantQuotaReservations.Where(item => item.TenantId == tenant && item.WindowStartUnixMilliseconds == start);
        var attempts = await window.CountAsync(token);
        var active = await context.TenantQuotaReservations.CountAsync(item => item.TenantId == tenant &&
            item.CompletedAtUnixMilliseconds == null && item.LeaseExpiresAtUnixMilliseconds > now, token);
        var tokens = await window.SumAsync(item => (long?)item.ChargedTokens, token) ?? 0;
        var unknownCost = await window.AnyAsync(item => item.ChargedCostMicroUnits == null, token);
        var cost = unknownCost ? (decimal?)null : (await window.SumAsync(item => item.ChargedCostMicroUnits, token) ?? 0) / 1000000m;
        var unknown = await window.CountAsync(item => item.UsageUnknown, token);
        return new(attempts, active, tokens, cost, unknown);
    }

    private static long WindowStart(long now, int seconds) => now - now % checked(seconds * 1000L);
    private static long? MicroUnits(decimal? cost) => cost is { } value ? checked((long)decimal.Ceiling(value * 1000000m)) : null;
}
