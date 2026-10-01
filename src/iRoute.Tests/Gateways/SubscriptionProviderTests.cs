using System.Text.Json;
using iRoute.Common;
using iRoute.Services;
using Xunit;

namespace iRoute.Tests.Gateways;

public sealed class SubscriptionProviderTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private static readonly string[] StatusArguments = ["auth", "status", "--json"];

    [Fact]
    public async Task ClaudeSubscriptionRunsOfficialCliWithoutToolsOrAPIBilling()
    {
        var runner = new RecordingRunner();
        var route = NativeProviderTests.Route(true) with { Adapter = "ClaudeCode" };
        var gateway = new ClaudeCodeSubscriptionGateway(runner, route);
        var result = await gateway.ExecuteAsync(NativeProviderTests.Request(), Token);
        Assert.False(result.Usage.CostKnown);
        Assert.Equal(1, result.Usage.ModelCalls);
        Assert.Equal(175, result.Usage.InputTokens);
        Assert.Equal(50, result.Usage.CachedInputTokens);
        Assert.Equal(3, runner.Calls.Count);
        Assert.Equal(StatusArguments, runner.Calls[1].Arguments);
        var invocation = runner.Calls[2];
        Assert.Contains("--safe-mode", invocation.Arguments);
        Assert.Contains("--strict-mcp-config", invocation.Arguments);
        Assert.Contains("--no-session-persistence", invocation.Arguments);
        Assert.Equal("", invocation.Arguments[invocation.Arguments.ToList().IndexOf("--tools") + 1]);
        Assert.DoesNotContain("test-key", invocation.StandardInput, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SubscriptionRejectsDollarBudgetsAndOtherTenantsBeforeStartingCli()
    {
        var runner = new RecordingRunner();
        var gateway = new ClaudeCodeSubscriptionGateway(runner, NativeProviderTests.Route(true) with { Adapter = "ClaudeCode" });
        await Assert.ThrowsAsync<ModelGatewayException>(() => gateway.ExecuteAsync(NativeProviderTests.Request() with { MaximumCost = 1m }, Token));
        await Assert.ThrowsAsync<ModelGatewayException>(() => gateway.ExecuteAsync(NativeProviderTests.Request() with { TenantId = "another" }, Token));
        Assert.Empty(runner.Calls);
    }

    [Fact]
    public async Task UnsupportedCliVersionFailsBeforeModelExecution()
    {
        var runner = new RecordingRunner { Help = "old version" };
        var gateway = new ClaudeCodeSubscriptionGateway(runner, NativeProviderTests.Route(true) with { Adapter = "ClaudeCode" });
        await Assert.ThrowsAsync<ModelGatewayException>(() => gateway.ExecuteAsync(NativeProviderTests.Request(), Token));
        Assert.Single(runner.Calls);
    }

    [Theory]
    [InlineData("{\"loggedIn\":true,\"authMethod\":\"api_key\",\"apiProvider\":\"firstParty\",\"subscriptionType\":\"pro\"}")]
    [InlineData("{\"loggedIn\":false,\"authMethod\":\"none\",\"apiProvider\":\"firstParty\"}")]
    [InlineData("{\"loggedIn\":true,\"authMethod\":\"claude.ai\",\"apiProvider\":\"firstParty\",\"subscriptionType\":\"pro\",\"apiKeySource\":\"helper\"}")]
    [InlineData("unsupported status format")]
    public async Task SubscriptionRequiresRecognizedPlanLoginBeforeInference(string status)
    {
        var runner = new RecordingRunner { Status = status };
        var gateway = new ClaudeCodeSubscriptionGateway(runner, NativeProviderTests.Route(true) with { Adapter = "ClaudeCode" });
        var error = await Assert.ThrowsAsync<ModelGatewayException>(() => gateway.ExecuteAsync(NativeProviderTests.Request(), Token));
        Assert.Equal(ModelGatewayFailureKind.Authentication, error.FailureKind);
        Assert.Equal(2, runner.Calls.Count);
        Assert.DoesNotContain(status, error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MissingProviderBinaryReportsActionableError()
    {
        var runner = new ProviderCliRunner(TimeProvider.System);
        var error = await Assert.ThrowsAsync<ModelGatewayException>(() => runner.RunAsync(
            new ProviderCliInvocation(Path.Combine(Path.GetTempPath(), $"missing-provider-{Guid.NewGuid():N}"), ["--help"], ""), Token));
        Assert.Equal(ErrorCodes.ModelGatewayUnavailable, error.Code);
        Assert.Contains("Install", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CancelledCallerDoesNotStartTheProviderProcess()
    {
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();
        var runner = new ProviderCliRunner(TimeProvider.System);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => runner.RunAsync(
            new ProviderCliInvocation("must-not-start", [], ""), cancellation.Token));
    }

    private sealed class RecordingRunner : IProviderCliRunner
    {
        public string Help { get; init; } = "--safe-mode --tools --strict-mcp-config --json-schema --no-session-persistence";
        public string Status { get; init; } = "{\"loggedIn\":true,\"authMethod\":\"claude.ai\",\"apiProvider\":\"firstParty\",\"subscriptionType\":\"pro\"}";
        public List<ProviderCliInvocation> Calls { get; } = [];

        public Task<ProviderCliResult> RunAsync(ProviderCliInvocation invocation, CancellationToken cancellationToken)
        {
            Calls.Add(invocation);
            return Task.FromResult(new ProviderCliResult(0, invocation.Arguments.Contains("--help") ? Help : invocation.Arguments.Contains("status") ? Status :
                JsonSerializer.Serialize(new { is_error = false, structured_output = new { subject = "Review", body = "Review this.", tone = "professional" }, usage = new { input_tokens = 100, cache_read_input_tokens = 50, cache_creation_input_tokens = 25, output_tokens = 20 } })));
        }
    }
}
