using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using iRoute.Common;
using iRoute.Services;
using Xunit;

namespace iRoute.Tests.Gateways;

public sealed class NativeProviderTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private const string TaskJson = "{\"subject\":\"Review\",\"body\":\"Please review.\",\"tone\":\"professional\"}";

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UsesNativeProtocolAndNormalizesStructuredOutputAndUsage(bool anthropic)
    {
        using var handler = new RecordingHandler(Success(anthropic));
        using var client = new HttpClient(handler);
        var route = Route(anthropic) with { InputCostPerMillionTokens = 2m, OutputCostPerMillionTokens = 10m };
        var result = await Gateway(client, route, anthropic).ExecuteAsync(Request(), Token);

        Assert.Equal("Review", result.Output.GetProperty("subject").GetString());
        Assert.Equal(100, result.Usage.InputTokens);
        Assert.Equal(20, result.Usage.OutputTokens);
        Assert.Equal(0.0004m, result.Usage.Cost);
        Assert.True(result.Usage.CostKnown);
        Assert.Equal(1, result.Usage.ModelCalls);
        Assert.EndsWith(anthropic ? "/prefix/v1/messages" : "/prefix/v1/responses", handler.Url, StringComparison.Ordinal);
        Assert.Equal(anthropic ? "test-key" : "Bearer test-key", handler.Credential);
        using var payload = JsonDocument.Parse(handler.Body!);
        Assert.Equal("test-model", payload.RootElement.GetProperty("model").GetString());
        Assert.Equal(100, payload.RootElement.GetProperty(anthropic ? "max_tokens" : "max_output_tokens").GetInt32());
        Assert.Contains("json_schema", handler.Body!, StringComparison.Ordinal);
        Assert.DoesNotContain("test-key", handler.Body!, StringComparison.Ordinal);
        if (!anthropic) Assert.False(payload.RootElement.TryGetProperty("reasoning", out _));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UnpricedUsageIsExplicitlyUnknownAndDollarLimitsFailBeforeSending(bool anthropic)
    {
        using var handler = new RecordingHandler(Success(anthropic));
        using var client = new HttpClient(handler);
        var gateway = Gateway(client, Route(anthropic), anthropic);
        var usage = (await gateway.ExecuteAsync(Request(), Token)).Usage;
        Assert.False(usage.CostKnown);
        var error = await Assert.ThrowsAsync<ModelGatewayException>(() =>
            gateway.ExecuteAsync(Request() with { MaximumCost = 1m }, Token));
        Assert.Equal(ErrorCodes.CostBudgetExceeded, error.Code);
        Assert.Equal(1, handler.Calls);
    }

    [Theory]
    [InlineData(401, false, GatewayFailureClass.Permanent)]
    [InlineData(429, true, GatewayFailureClass.Throttling)]
    [InlineData(503, true, GatewayFailureClass.Provider)]
    public async Task ProviderFailuresAreClassifiedWithoutExposingTheResponseBody(int status, bool retryable, GatewayFailureClass failure)
    {
        using var handler = new RecordingHandler("secret-provider-error", (HttpStatusCode)status);
        using var client = new HttpClient(handler);
        var error = await Assert.ThrowsAsync<ModelGatewayException>(() => Gateway(client, Route(false), false).ExecuteAsync(Request(), Token));
        Assert.Equal(status, error.StatusCode);
        Assert.Equal(retryable, error.Retryable);
        Assert.Equal(failure, error.FailureClass);
        Assert.Equal(TimeSpan.FromSeconds(120), error.RetryAfter);
        Assert.DoesNotContain("secret-provider-error", error.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("not-json")]
    [InlineData("{\"status\":\"incomplete\"}")]
    [InlineData("{\"status\":\"completed\",\"output\":[{\"type\":\"message\",\"content\":[{\"type\":\"refusal\"}]}]}")]
    public async Task MalformedTruncatedAndRefusedOutputsAreRejected(string response)
    {
        using var handler = new RecordingHandler(response);
        using var client = new HttpClient(handler);
        var error = await Assert.ThrowsAsync<ModelGatewayException>(() => Gateway(client, Route(false), false).ExecuteAsync(Request(), Token));
        Assert.Equal(ErrorCodes.ModelGatewayInvalidResponse, error.Code);
    }

    [Fact]
    public async Task OversizedResponsesAreRejected()
    {
        using var handler = new RecordingHandler(new string('x', 2 * 1024 * 1024 + 1));
        using var client = new HttpClient(handler);
        var error = await Assert.ThrowsAsync<ModelGatewayException>(() => Gateway(client, Route(false), false).ExecuteAsync(Request(), Token));
        Assert.Equal(ErrorCodes.ModelGatewayInvalidResponse, error.Code);
    }

    [Theory]
    [InlineData("{\"subject\":7,\"body\":\"Review\",\"tone\":\"professional\"}")]
    [InlineData("{\"subject\":\"Review\",\"body\":\"Review\",\"tone\":\"professional\",\"unexpected\":true}")]
    public async Task StructuredOutputMustMatchTheRequestedFieldTypesAndShape(string taskJson)
    {
        using var handler = new RecordingHandler(Success(false, taskJson));
        using var client = new HttpClient(handler);
        var error = await Assert.ThrowsAsync<ModelGatewayException>(() =>
            Gateway(client, Route(false), false).ExecuteAsync(Request(), Token));
        Assert.Equal(ErrorCodes.ModelGatewayInvalidResponse, error.Code);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ProviderCannotReportMoreOutputTokensThanTheTaskAllows(bool anthropic)
    {
        using var handler = new RecordingHandler(Success(anthropic));
        using var client = new HttpClient(handler);
        var error = await Assert.ThrowsAsync<ModelGatewayException>(() =>
            Gateway(client, Route(anthropic), anthropic).ExecuteAsync(Request() with { MaxOutputTokens = 10 }, Token));
        Assert.Equal(ErrorCodes.ModelGatewayInvalidResponse, error.Code);
    }

    [Fact]
    public async Task ProviderDeadlineStopsTheRequestAndReportsRetryableTimeout()
    {
        using var handler = new WaitingHandler();
        using var client = new HttpClient(handler);
        var error = await Assert.ThrowsAsync<ModelGatewayException>(() =>
            Gateway(client, Route(false), false).ExecuteAsync(Request() with { DeadlineMilliseconds = 100 }, Token));
        Assert.Equal(GatewayFailureClass.Timeout, error.FailureClass);
        Assert.True(error.Retryable);
    }

    [Fact]
    public async Task CallerCancellationRemainsCancellationRatherThanProviderFailure()
    {
        using var handler = new WaitingHandler();
        using var client = new HttpClient(handler);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(Token);
        var pending = Gateway(client, Route(false), false).ExecuteAsync(Request(), cancellation.Token);
        await handler.Started.Task.WaitAsync(Token);
        await cancellation.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
    }

    [Fact]
    public async Task ChatGPTPlanUsesOnlyTheApplicationsOwnPlanTokenAndStaysTenantScoped()
    {
        using var handler = new RecordingHandler("data: " + JsonSerializer.Serialize(new { type = "response.completed", response = JsonSerializer.Deserialize<JsonElement>(Success(false)) }) + "\n\n",
            mediaType: "text/event-stream");
        using var client = new HttpClient(handler);
        var route = Route(false) with { Adapter = "OpenAIChatGPT", ChatGPTAccessToken = "own-plan-token", SubscriptionTenantId = "owner" };
        var gateway = Gateway(client, route, false);
        await gateway.ExecuteAsync(Request() with { TenantId = "owner" }, Token);
        Assert.Equal("Bearer own-plan-token", handler.Credential);
        using var payload = JsonDocument.Parse(handler.Body!);
        Assert.True(payload.RootElement.GetProperty("stream").GetBoolean());
        Assert.False(payload.RootElement.GetProperty("store").GetBoolean());
        Assert.False(payload.RootElement.TryGetProperty("max_output_tokens", out _));
        var input = Assert.Single(payload.RootElement.GetProperty("input").EnumerateArray());
        Assert.Equal("user", input.GetProperty("role").GetString());
        Assert.Contains("Review", input.GetProperty("content").GetString(), StringComparison.Ordinal);
        var error = await Assert.ThrowsAsync<ModelGatewayException>(() => gateway.ExecuteAsync(Request(), Token));
        Assert.Equal(ErrorCodes.PermissionScopeDenied, error.Code);
        Assert.Equal(1, handler.Calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReasoningEffortIsExplicitlyConfiguredRatherThanForcedAcrossAllModels(bool subscription)
    {
        var response = subscription ? "data: " + JsonSerializer.Serialize(new { type = "response.completed", response = JsonSerializer.Deserialize<JsonElement>(Success(false)) }) + "\n\n"
            : Success(false);
        using var handler = new RecordingHandler(response, mediaType: subscription ? "text/event-stream" : "application/json");
        using var client = new HttpClient(handler);
        var route = Route(false) with { Adapter = subscription ? "OpenAIChatGPT" : "OpenAI", ChatGPTAccessToken = "own-plan-token", ReasoningEffort = "low" };
        await Gateway(client, route, false).ExecuteAsync(Request(), Token);
        using var payload = JsonDocument.Parse(handler.Body!);
        Assert.Equal("low", payload.RootElement.GetProperty("reasoning").GetProperty("effort").GetString());
        Assert.Equal(!subscription, payload.RootElement.TryGetProperty("max_output_tokens", out _));
    }

    [Theory]
    [InlineData("data: {\"type\":\"response.output_text.delta\",\"delta\":\"partial\"}\n\n")]
    [InlineData("data: {\"type\":\"response.incomplete\"}\n\n")]
    [InlineData("data: {\"type\":\"response.failed\",\"response\":{\"error\":{\"code\":\"subscription_sharing_usage_limit_exceeded\",\"message\":\"secret\"}}}\n\n")]
    public async Task ChatGPTStreamRequiresCompletionAndRedactsFailures(string events)
    {
        using var handler = new RecordingHandler(events, mediaType: "text/event-stream");
        using var client = new HttpClient(handler);
        var route = Route(false) with { Adapter = "OpenAIChatGPT", ChatGPTAccessToken = "own-plan-token" };
        var error = await Assert.ThrowsAsync<ModelGatewayException>(() => Gateway(client, route, false).ExecuteAsync(Request(), Token));
        Assert.False(error.Retryable);
        Assert.DoesNotContain("secret", error.ToString(), StringComparison.Ordinal);
    }

    internal static ModelGatewayRequest Request() => new("text.generation",
        JsonSerializer.SerializeToElement(new { objective = "Review" }), JsonSerializer.SerializeToElement(new { }), 100);

    internal static ModelGatewayDeploymentOptions Route(bool anthropic) => new()
    {
        Adapter = anthropic ? "Anthropic" : "OpenAI",
        GatewayId = "native-test",
        ApiKey = "test-key",
        Model = "test-model",
        BaseUrl = "http://localhost/prefix/v1/",
        ExpectedQuality = 0.9m
    };

    private static IModelGateway Gateway(HttpClient client, ModelGatewayDeploymentOptions route, bool anthropic)
    {
        var transport = new ProviderHttpTransport(client, route, TimeProvider.System);
        return anthropic ? new AnthropicModelGateway(transport, route) : new OpenAIModelGateway(transport, route);
    }

    private static string Success(bool anthropic, string taskJson = TaskJson) => anthropic
        ? JsonSerializer.Serialize(new { stop_reason = "end_turn", content = new[] { new { type = "text", text = taskJson } }, usage = new { input_tokens = 100, output_tokens = 20 } })
        : JsonSerializer.Serialize(new { status = "completed", output = new[] { new { type = "message", content = new[] { new { type = "output_text", text = taskJson } } } }, usage = new { input_tokens = 100, output_tokens = 20 } });

    private sealed class WaitingHandler : HttpMessageHandler
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Started.SetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException("The cancelled request must not complete.");
        }
    }

    private sealed class RecordingHandler(string response, HttpStatusCode status = HttpStatusCode.OK, string mediaType = "application/json") : HttpMessageHandler
    {
        public int Calls { get; private set; }
        public string? Url { get; private set; }
        public string? Body { get; private set; }
        public string? Credential { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            Url = request.RequestUri!.AbsoluteUri;
            Body = await request.Content!.ReadAsStringAsync(cancellationToken);
            Credential = request.Headers.Authorization?.ToString() ?? request.Headers.GetValues("x-api-key").Single();
            var result = new HttpResponseMessage(status) { Content = new StringContent(response, Encoding.UTF8, mediaType) };
            result.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(120));
            return result;
        }
    }
}
