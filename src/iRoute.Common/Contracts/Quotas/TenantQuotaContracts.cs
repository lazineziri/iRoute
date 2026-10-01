namespace iRoute.Common;

public sealed record TenantQuotaPolicy
{
    public int MaxConcurrentExecutions { get; init; } = 8;
    public int MaxConcurrentModelCalls { get; init; } = 4;
    public int MaxModelAttemptsPerWindow { get; init; } = 60;
    public long MaxTokensPerWindow { get; init; } = 120000;
    public decimal? MaxCostPerWindow { get; init; }

    public void EnsureValid()
    {
        if (MaxConcurrentExecutions < 1 || MaxConcurrentModelCalls < 1 || MaxModelAttemptsPerWindow < 1 ||
            MaxTokensPerWindow < 1 || MaxCostPerWindow is <= 0 or > 1000000)
            throw new InvalidOperationException("Tenant quotas require positive concurrency, attempt, token and optional cost limits.");
    }
}

public sealed record TenantQuotaOptions
{
    public bool Enabled { get; init; }
    public bool FairSchedulingEnabled { get; init; } = true;
    public int WindowSeconds { get; init; } = 60;
    public int FramingTokenReserve { get; init; } = 4096;
    public TenantQuotaPolicy Default { get; init; } = new();
    public Dictionary<string, TenantQuotaPolicy> Tenants { get; init; } = new(StringComparer.Ordinal);

    public TenantQuotaPolicy ForTenant(string tenant) => Tenants.GetValueOrDefault(tenant) ?? Default;

    public void EnsureValid()
    {
        if (WindowSeconds is < 1 or > 3600 || FramingTokenReserve is < 0 or > 1000000)
            throw new InvalidOperationException("Quota windows must be 1–3600 seconds and framing reserve bounded.");
        Default.EnsureValid();
        foreach (var (tenant, policy) in Tenants)
        {
            if (string.IsNullOrWhiteSpace(tenant) || tenant.Length > 200)
                throw new InvalidOperationException("Quota tenant keys must be non-empty and at most 200 characters.");
            policy.EnsureValid();
        }
    }
}

public sealed record TenantQuotaReservation(Guid ReservationId, string TenantId, long ReservedTokens,
    decimal? ReservedCost, DateTimeOffset CreatedAt, DateTimeOffset LeaseExpiresAt, int WindowSeconds);

public sealed record TenantQuotaPermit(bool Accepted, string? Reason = null, TimeSpan? RetryAfter = null);

public sealed record TenantQuotaSnapshot(int Attempts, int ActiveModelCalls, long ChargedTokens,
    decimal? ChargedCost, int UnknownUsageAttempts);

public interface ITenantQuotaStore
{
    Task<TenantQuotaPermit> TryReserveAsync(TenantQuotaReservation reservation, TenantQuotaPolicy policy, CancellationToken token);
    Task ReconcileAsync(Guid reservationId, UsageSummary? usage, DateTimeOffset completedAt, CancellationToken token);
    Task<TenantQuotaSnapshot> InspectAsync(string tenant, DateTimeOffset at, int windowSeconds, CancellationToken token);
}
