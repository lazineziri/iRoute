using System.Net;
using System.Runtime.CompilerServices;
using System.Text.Json;
using iRoute.Common;
using iRoute.Services;
using iRoute.Tests.Executions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace iRoute.Tests.Quotas;

public sealed class QuotaGatewayTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private static ModelGatewayRequest Request => new("text.generation", JsonSerializer.SerializeToElement(new { }),
        JsonSerializer.SerializeToElement(new { }), 100, TenantId: "tenant");

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConfiguredAdaptersCannotBypassQuotasThroughResilienceOrFallback(bool resilient)
    {
        var handler = new CountingHandler();
        await using var fixture = await ExecutionFixture.CreateAsync(configure: services =>
        {
            services.AddSingleton(new TenantQuotaOptions { Enabled = true, Default = new() { MaxTokensPerWindow = 1 } });
            services.AddSingleton<IOptions<ModelGatewayOptions>>(Options.Create(new ModelGatewayOptions
            {
                Mode = "OpenAI",
                Model = "test-model",
                ApiKey = "synthetic-test-key",
                Resilience = new() { Enabled = resilient }
            }));
            services.AddHttpClient("iroute-generic-gateway").ConfigurePrimaryHttpMessageHandler(() => handler);
        });
        var error = await Assert.ThrowsAsync<ModelGatewayException>(() =>
            fixture.Services.GetRequiredService<IModelGateway>().ExecuteAsync(Request, Token));
        Assert.Equal(ErrorCodes.TenantQuotaExceeded, error.Code);
        Assert.Equal(0, handler.Calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StreamingPreservesDeltasAndOnlyReconcilesCompleteUsage(bool stopEarly)
    {
        await using var fixture = await ExecutionFixture.CreateAsync();
        var inner = new ControlledGateway();
        var store = fixture.Services.GetRequiredService<ITenantQuotaStore>();
        var gateway = new QuotaModelGateway(inner, store, new() { Enabled = true }, new(), TimeProvider.System);
        var events = new List<ModelGatewayStreamEvent>();
        await foreach (var item in gateway.StreamAsync(Request, Token))
        {
            events.Add(item);
            if (stopEarly) break;
        }
        Assert.Equal(0, inner.Calls);
        Assert.Equal("delta", events[0].Delta);
        var state = await store.InspectAsync("tenant", DateTimeOffset.UtcNow, 60, Token);
        Assert.Equal(0, state.ActiveModelCalls);
        if (stopEarly) { Assert.Equal(1, state.UnknownUsageAttempts); Assert.True(state.ChargedTokens >= 4196); }
        else { Assert.Equal(30, state.ChargedTokens); Assert.Equal(2, events.Count); }
    }

    [Fact]
    public async Task DenialStopsBeforeTheProviderAndUsesTheTenantOverride()
    {
        await using var fixture = await ExecutionFixture.CreateAsync();
        var inner = new ControlledGateway();
        var store = fixture.Services.GetRequiredService<ITenantQuotaStore>();
        var options = new TenantQuotaOptions
        {
            Enabled = true,
            Tenants = new()
            { ["tenant"] = new TenantQuotaPolicy { MaxTokensPerWindow = 1 } }
        };
        var gateway = new QuotaModelGateway(inner, store, options, new(), TimeProvider.System);
        var error = await Assert.ThrowsAsync<ModelGatewayException>(() => gateway.ExecuteAsync(Request, Token));
        Assert.Equal(ErrorCodes.TenantQuotaExceeded, error.Code);
        Assert.Equal(GatewayFailureClass.Policy, error.FailureClass);
        Assert.NotNull(error.RetryAfter);
        Assert.Equal(0, inner.Calls);
        Assert.Equal(0, (await store.InspectAsync("tenant", DateTimeOffset.UtcNow, 60, Token)).Attempts);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailedOrCancelledCallsRetainUnknownUsageButReleaseConcurrency(bool cancelled)
    {
        await using var fixture = await ExecutionFixture.CreateAsync();
        var inner = new ControlledGateway(cancelled ? "cancel" : "fail");
        var store = fixture.Services.GetRequiredService<ITenantQuotaStore>();
        var gateway = new QuotaModelGateway(inner, store, new() { Enabled = true }, new(), TimeProvider.System);
        if (cancelled) await Assert.ThrowsAnyAsync<OperationCanceledException>(() => gateway.ExecuteAsync(Request, Token));
        else await Assert.ThrowsAsync<ModelGatewayException>(() => gateway.ExecuteAsync(Request, Token));
        var state = await store.InspectAsync("tenant", DateTimeOffset.UtcNow, 60, Token);
        Assert.Equal(0, state.ActiveModelCalls);
        Assert.Equal(1, state.UnknownUsageAttempts);
        Assert.True(state.ChargedTokens >= 4196);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SuccessAndReportedOverrunsAreReconciledUsingActualProviderUsage(bool overrun)
    {
        await using var fixture = await ExecutionFixture.CreateAsync();
        var store = fixture.Services.GetRequiredService<ITenantQuotaStore>();
        var gateway = new QuotaModelGateway(new ControlledGateway(overrun ? "overrun" : "success"), store,
            new() { Enabled = true }, new(), TimeProvider.System);
        if (overrun) await Assert.ThrowsAsync<ModelGatewayException>(() => gateway.ExecuteAsync(Request, Token));
        else await gateway.ExecuteAsync(Request, Token);
        var state = await store.InspectAsync("tenant", DateTimeOffset.UtcNow, 60, Token);
        Assert.Equal(overrun ? 10020 : 30, state.ChargedTokens);
        Assert.Equal(0, state.ActiveModelCalls);
        Assert.Equal(0, state.UnknownUsageAttempts);
        Assert.Null(state.ChargedCost);
    }

    [Fact]
    public async Task SubscriptionCannotPassADollarQuotaUsingZeroAsAPrice()
    {
        await using var fixture = await ExecutionFixture.CreateAsync();
        var inner = new ControlledGateway();
        var gateway = new QuotaModelGateway(inner, fixture.Services.GetRequiredService<ITenantQuotaStore>(),
            new() { Enabled = true, Default = new() { MaxCostPerWindow = 1 } },
            new() { Adapter = "OpenAIChatGPT", InputCostPerMillionTokens = 0, OutputCostPerMillionTokens = 0 }, TimeProvider.System);
        await Assert.ThrowsAsync<ModelGatewayException>(() => gateway.ExecuteAsync(Request, Token));
        Assert.Equal(0, inner.Calls);
    }

    private sealed class ControlledGateway(string behavior = "success") : IModelGateway
    {
        public int Calls { get; private set; }
        public async IAsyncEnumerable<ModelGatewayStreamEvent> StreamAsync(ModelGatewayRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return new(1, ModelGatewayStreamEventKind.OutputDelta, Delta: "delta");
            await Task.Yield();
            yield return new(2, ModelGatewayStreamEventKind.Completed, Result: new ModelGatewayResult(
                JsonSerializer.SerializeToElement(new { }), new UsageSummary(20, 10, CostKnown: false), 0.9m, []));
        }
        public Task<ModelGatewayResult> ExecuteAsync(ModelGatewayRequest request, CancellationToken cancellationToken)
        {
            Calls++;
            if (behavior == "cancel") throw new OperationCanceledException(cancellationToken);
            if (behavior == "fail") throw new ModelGatewayException(ErrorCodes.ModelGatewayUnavailable, "Controlled failure", false);
            var usage = new UsageSummary(20, behavior == "overrun" ? 10000 : 10, CostKnown: false);
            if (behavior == "overrun") throw new ModelGatewayException(ErrorCodes.ModelGatewayInvalidResponse,
                "Controlled overrun", false, reportedUsage: usage);
            return Task.FromResult(new ModelGatewayResult(JsonSerializer.SerializeToElement(new { }), usage, 0.9m, []));
        }
    }

    private sealed class CountingHandler : HttpMessageHandler
    {
        public int Calls { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        { Calls++; return Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError)); }
    }
}
