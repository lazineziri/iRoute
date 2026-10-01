using iRoute.Common;
using iRoute.Tests.Executions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace iRoute.Tests.Quotas;

public sealed class TenantQuotaTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private static readonly DateTimeOffset Now = DateTimeOffset.FromUnixTimeSeconds(1800000005);

    private static TenantQuotaReservation Reservation(string tenant = "quota-tenant", long tokens = 100, decimal? cost = 0.1m) =>
        new(Guid.CreateVersion7(), tenant, tokens, cost, Now, Now.AddSeconds(30), 60);

    [Fact]
    public async Task ConcurrentReservationsCannotExceedTheTenantLimit()
    {
        await using var fixture = await ExecutionFixture.CreateAsync();
        var quotas = fixture.Services.GetRequiredService<ITenantQuotaStore>();
        var policy = new TenantQuotaPolicy { MaxConcurrentModelCalls = 2 };
        var permits = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => quotas.TryReserveAsync(Reservation(), policy, Token)));
        Assert.Equal(2, permits.Count(item => item.Accepted));
        var state = await quotas.InspectAsync("quota-tenant", Now, 60, Token);
        Assert.Equal(2, state.ActiveModelCalls);
        Assert.Equal(200, state.ChargedTokens);
        Assert.True((await quotas.TryReserveAsync(Reservation("another-tenant"), policy, Token)).Accepted);
    }

    [Fact]
    public async Task SuccessfulUsageReconcilesWithoutDoubleCountingAndFreesConcurrency()
    {
        await using var fixture = await ExecutionFixture.CreateAsync();
        var quotas = fixture.Services.GetRequiredService<ITenantQuotaStore>();
        var request = Reservation();
        Assert.True((await quotas.TryReserveAsync(request, new TenantQuotaPolicy { MaxConcurrentModelCalls = 1 }, Token)).Accepted);
        var usage = new UsageSummary(20, 10, 0.02m, ModelCalls: 1, CachedInputTokens: 5, ReasoningTokens: 3);
        await quotas.ReconcileAsync(request.ReservationId, usage, Now.AddSeconds(1), Token);
        await quotas.ReconcileAsync(request.ReservationId, usage, Now.AddSeconds(2), Token);
        var state = await quotas.InspectAsync(request.TenantId, Now.AddSeconds(2), 60, Token);
        Assert.Equal(30, state.ChargedTokens);
        Assert.Equal(0.02m, state.ChargedCost);
        Assert.Equal(0, state.ActiveModelCalls);
        Assert.Equal(0, state.UnknownUsageAttempts);
        Assert.True((await quotas.TryReserveAsync(Reservation(), new TenantQuotaPolicy { MaxConcurrentModelCalls = 1 }, Token)).Accepted);
    }

    [Theory]
    [InlineData("attempt-rate")]
    [InlineData("tokens")]
    [InlineData("cost")]
    public async Task RateTokenAndCostLimitsRemainChargedAfterCompletion(string expected)
    {
        await using var fixture = await ExecutionFixture.CreateAsync();
        var quotas = fixture.Services.GetRequiredService<ITenantQuotaStore>();
        var policy = new TenantQuotaPolicy
        {
            MaxModelAttemptsPerWindow = expected == "attempt-rate" ? 1 : 10,
            MaxTokensPerWindow = expected == "tokens" ? 150 : 10000,
            MaxCostPerWindow = expected == "cost" ? 0.15m : null
        };
        var first = Reservation();
        Assert.True((await quotas.TryReserveAsync(first, policy, Token)).Accepted);
        await quotas.ReconcileAsync(first.ReservationId, null, Now.AddSeconds(1), Token);
        var denied = await quotas.TryReserveAsync(Reservation(), policy, Token);
        Assert.False(denied.Accepted);
        Assert.Equal(expected, denied.Reason);
        Assert.NotNull(denied.RetryAfter);
        Assert.Equal(100, (await quotas.InspectAsync(first.TenantId, Now, 60, Token)).ChargedTokens);
    }

    [Fact]
    public async Task UnknownCostCannotBypassADollarQuotaAndExpiredLeasesDoNotBlockForever()
    {
        await using var fixture = await ExecutionFixture.CreateAsync();
        var quotas = fixture.Services.GetRequiredService<ITenantQuotaStore>();
        var policy = new TenantQuotaPolicy { MaxConcurrentModelCalls = 1, MaxCostPerWindow = 1 };
        var unknown = await quotas.TryReserveAsync(Reservation(cost: null), policy, Token);
        Assert.False(unknown.Accepted);
        Assert.Equal("unknown-cost", unknown.Reason);
        var first = Reservation();
        Assert.True((await quotas.TryReserveAsync(first, policy, Token)).Accepted);
        var next = Reservation() with { CreatedAt = Now.AddSeconds(31), LeaseExpiresAt = Now.AddSeconds(61) };
        Assert.True((await quotas.TryReserveAsync(next, policy, Token)).Accepted);
        var state = await quotas.InspectAsync(first.TenantId, Now.AddSeconds(31), 60, Token);
        Assert.Equal(200, state.ChargedTokens);
        Assert.Equal(1, state.ActiveModelCalls);
    }
}
