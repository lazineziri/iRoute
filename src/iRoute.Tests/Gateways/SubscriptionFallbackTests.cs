using iRoute.Common;
using iRoute.Services;
using iRoute.Tests.Executions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace iRoute.Tests.Gateways;

public sealed class SubscriptionFallbackTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AFailedOrOpenSubscriptionCannotSilentlySpendOnAnApiRoute(bool alreadyOpen)
    {
        await using var fixture = await ExecutionFixture.CreateAsync();
        var clients = new Clients();
        var circuits = fixture.Services.GetRequiredService<IGatewayCircuitStore>();
        var options = new GatewayResilienceOptions { Circuit = new(FailureThreshold: 1) };
        if (alreadyOpen)
        {
            var now = DateTimeOffset.UtcNow;
            var permit = await circuits.TryAcquireAsync("subscription", "test", options.Circuit, now, TestContext.Current.CancellationToken);
            await circuits.RecordFailureAsync(permit, GatewayFailureClass.Provider, true, null,
                options.Circuit, now, TestContext.Current.CancellationToken);
        }
        var gateway = new ResilientModelGateway(new Registry(), clients, circuits, TimeProvider.System, options);
        var error = await Assert.ThrowsAsync<ModelGatewayException>(() => gateway.ExecuteAsync(
            NativeProviderTests.Request(), TestContext.Current.CancellationToken));
        Assert.Equal(ErrorCodes.ModelGatewayExhausted, error.Code);
        Assert.Equal(alreadyOpen ? 0 : 1, clients.SubscriptionCalls);
        Assert.Equal(0, clients.ApiCalls);
        Assert.Contains(error.Resilience!.Candidates, item => item.Reason.Contains("API-dollar billing", StringComparison.Ordinal));
    }

    private sealed class Registry : IGatewayDeploymentRegistry
    {
        public Task<IReadOnlyList<GatewayDeployment>> ListAsync(CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<GatewayDeployment>>(
        [
            new("subscription", "subscription", "ClaudeCode", "subscription", "unspecified", "unspecified", "test", ["*"], ["*"], 1m, 0m, 0, SubscriptionBilling: true),
            new("api", "api", "OpenAI", "api", "unspecified", "unspecified", "test", ["*"], ["*"], 1m, 0m, 0, Priority: 200)
        ]);
    }

    private sealed class Clients : IGatewayDeploymentClientFactory
    {
        public int SubscriptionCalls { get; set; }
        public int ApiCalls { get; set; }
        public IModelGateway GetClient(GatewayDeployment deployment) => new FailingGateway(this, deployment.SubscriptionBilling);
    }

    private sealed class FailingGateway(Clients clients, bool subscription) : IModelGateway
    {
        public Task<ModelGatewayResult> ExecuteAsync(ModelGatewayRequest request, CancellationToken cancellationToken)
        {
            if (subscription) clients.SubscriptionCalls++;
            else clients.ApiCalls++;
            throw new ModelGatewayException(ErrorCodes.ModelGatewayUnavailable, "Subscription quota unavailable.", true,
                failureClass: GatewayFailureClass.Provider);
        }
    }
}
