using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using iRoute.Common;
using iRoute.Services;
using Xunit;

namespace iRoute.Tests.Gateways;

public sealed class ChatGPTResponseStreamTests
{
    private const string TaskJson = "{\"subject\":\"Révision ✅\",\"body\":\"Please review.\",\"tone\":\"professional\"}";
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private static readonly JsonSerializerOptions WireJson = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    [Theory]
    [InlineData(null, "\n", 1)]
    [InlineData("text/event-stream", "\r\n", 7)]
    [InlineData("Text/Event-Stream", "\r", 3)]
    public async Task ReconstructsFragmentedTextWithEmptyTerminalOutput(string? mediaType, string newline, int chunk)
    {
        var events = ": heartbeat\n\n" + Delta(TaskJson[..17]) + Delta(TaskJson[17..]) + Done(TaskJson) + ItemDone(TaskJson) + Completed();
        using var handler = new StreamHandler(events.Replace("\n", newline, StringComparison.Ordinal), mediaType, chunk);
        using var client = new HttpClient(handler);
        var result = await Gateway(client).ExecuteAsync(NativeProviderTests.Request(), Token);

        Assert.Equal("Révision ✅", result.Output.GetProperty("subject").GetString());
        Assert.Equal(147, result.Usage.InputTokens);
        Assert.Equal(43, result.Usage.OutputTokens);
        Assert.False(result.Usage.CostKnown);
        Assert.Equal("text/event-stream", handler.Accept);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CompletedSnapshotsAndFinalTextDoNotDuplicateDeltas(bool snapshot)
    {
        using var handler = new StreamHandler(Delta(TaskJson) + Done(TaskJson) + Completed(snapshot));
        using var client = new HttpClient(handler);
        var result = await Gateway(client).ExecuteAsync(NativeProviderTests.Request(), Token);
        Assert.Equal("Please review.", result.Output.GetProperty("body").GetString());
    }

    [Fact]
    public async Task FinalItemWithoutTextDeltasIsSupported()
    {
        using var handler = new StreamHandler(ItemDone(TaskJson) + Completed());
        using var client = new HttpClient(handler);
        var result = await Gateway(client).ExecuteAsync(NativeProviderTests.Request(), Token);
        Assert.Equal("professional", result.Output.GetProperty("tone").GetString());
    }

    [Fact]
    public async Task SummariesUseTheRequestedStructuredOutputContract()
    {
        const string summary = "{\"summary\":\"Friday at 10:00.\",\"highlights\":[\"No external actions.\"]}";
        using var handler = new StreamHandler(Delta(summary) + Completed());
        using var client = new HttpClient(handler);
        var result = await Gateway(client).ExecuteAsync(NativeProviderTests.Request() with { Capability = "text.summarization" }, Token);
        Assert.Equal("Friday at 10:00.", result.Output.GetProperty("summary").GetString());
    }

    [Fact]
    public async Task ContentPartsAreAssembledInIndexOrderRatherThanArrivalOrder()
    {
        var second = Delta(TaskJson[17..]).Replace("\"content_index\":0", "\"content_index\":1", StringComparison.Ordinal);
        using var handler = new StreamHandler(second + Delta(TaskJson[..17]) + Completed());
        using var client = new HttpClient(handler);
        var result = await Gateway(client).ExecuteAsync(NativeProviderTests.Request(), Token);
        Assert.Equal("Révision ✅", result.Output.GetProperty("subject").GetString());
    }

    [Fact]
    public async Task MultipleDataLinesFormOneJsonEvent()
    {
        var completed = "data: {\"type\":\"response.completed\",\n" +
            "data: \"response\":{\"status\":\"completed\",\"output\":[],\"usage\":{\"input_tokens\":147,\"output_tokens\":43}}}\n\n";
        using var handler = new StreamHandler(Delta(TaskJson) + completed);
        using var client = new HttpClient(handler);
        Assert.True((await Gateway(client).ExecuteAsync(NativeProviderTests.Request(), Token)).Usage.OutputTokens > 0);
    }

    [Theory]
    [InlineData("")]
    [InlineData("data: [DONE]\n\n")]
    [InlineData("data: {\"type\":\"response.completed\"}\n\n")]
    [InlineData("data: []\n\n")]
    [InlineData("data: {\"type\":\"response.completed\",\"response\":{\"status\":\"incomplete\",\"output\":[]}}\n\n")]
    [InlineData("data: {\"type\":\"response.completed\",\"response\":{\"status\":\"completed\",\"output\":[]}}\n\n")]
    [InlineData("data: {\"type\":\"response.refusal.delta\",\"delta\":\"secret\"}\n\n")]
    [InlineData("data: {\"type\":\"response.output_item.done\",\"item\":{\"type\":\"function_call\",\"arguments\":\"secret\"}}\n\n")]
    [InlineData("data: {\"type\":\"response.output_text.delta\",\"output_index\":-1,\"content_index\":0,\"delta\":\"secret\"}\n\n")]
    [InlineData("data: {\"type\":\"response.output_text.delta\",\"output_index\":0,\"content_index\":0,\"delta\":7}\n\n")]
    [InlineData("data: {\"type\":\"response.content_part.done\",\"part\":{\"type\":\"refusal\",\"refusal\":\"secret\"}}\n\n")]
    [InlineData("data: {\"type\":\"response.completed\",\"response\":{\"status\":\"completed\",\"output\":[],\"usage\":{\"input_tokens\":147,\"output_tokens\":-1}}}\n\n")]
    public async Task MalformedRefusedUnsupportedAndUnfinishedStreamsFailClosed(string ending)
    {
        await AssertInvalidAsync(Delta(TaskJson) + ending);
    }

    [Theory]
    [InlineData("{\"status\":\"completed\",\"output\":[]}", null)]
    [InlineData("<html>secret</html>", null)]
    [InlineData("data: {secret}\n\n", null)]
    [InlineData("", null)]
    [InlineData("ignored", "application/json")]
    [InlineData("ignored", "text/html")]
    public async Task MissingHeadersNeverEnableAJsonOrHtmlFallback(string events, string? mediaType)
    {
        await AssertInvalidAsync(events, mediaType);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConflictingFinalTextIsRejected(bool snapshot)
    {
        await AssertInvalidAsync(Delta(TaskJson) + (snapshot ? Completed(true, TaskJson.Replace("review", "secret", StringComparison.Ordinal))
            : Done(TaskJson.Replace("review", "secret", StringComparison.Ordinal)) + Completed()));
    }

    [Fact]
    public async Task TokenLimitIsCheckedAfterCompletionEvenThoughThePlanRejectsTheRequestParameter()
    {
        using var handler = new StreamHandler(Delta(TaskJson) + Completed());
        using var client = new HttpClient(handler);
        var error = await Assert.ThrowsAsync<ModelGatewayException>(() =>
            Gateway(client).ExecuteAsync(NativeProviderTests.Request() with { MaxOutputTokens = 10 }, Token));
        Assert.Equal(ErrorCodes.ModelGatewayInvalidResponse, error.Code);
    }

    [Theory]
    [InlineData("subscription_sharing_usage_limit_exceeded", false)]
    [InlineData("subscription_sharing_usage_unavailable", true)]
    public async Task PlanFailuresKeepTheirRetryClassificationAndRedactProviderMessages(string code, bool retryable)
    {
        var events = Event(new { type = "response.failed", response = new { error = new { code, message = "secret" } } });
        using var handler = new StreamHandler(events);
        using var client = new HttpClient(handler);
        var error = await Assert.ThrowsAsync<ModelGatewayException>(() => Gateway(client).ExecuteAsync(NativeProviderTests.Request(), Token));
        Assert.Equal(retryable, error.Retryable);
        Assert.Equal(retryable ? ModelGatewayFailureKind.Unavailable : ModelGatewayFailureKind.RateLimited, error.FailureKind);
        Assert.DoesNotContain("secret", error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ChunkedStreamsRemainBoundedWithoutAContentLengthHeader()
    {
        await AssertInvalidAsync("data: " + new string('x', 2 * 1024 * 1024));
    }

    [Fact]
    public async Task InvalidUtf8CannotBeSilentlyReplaced()
    {
        using var handler = new StreamHandler("data: ", suffix: new byte[] { 0xff, 10, 10 });
        using var client = new HttpClient(handler);
        var error = await Assert.ThrowsAsync<ModelGatewayException>(() => Gateway(client).ExecuteAsync(NativeProviderTests.Request(), Token));
        Assert.Equal(ErrorCodes.ModelGatewayInvalidResponse, error.Code);
    }

    private static async Task AssertInvalidAsync(string events, string? mediaType = "text/event-stream")
    {
        using var handler = new StreamHandler(events, mediaType);
        using var client = new HttpClient(handler);
        var error = await Assert.ThrowsAsync<ModelGatewayException>(() => Gateway(client).ExecuteAsync(NativeProviderTests.Request(), Token));
        Assert.Equal(ErrorCodes.ModelGatewayInvalidResponse, error.Code);
        Assert.DoesNotContain("secret", error.ToString(), StringComparison.Ordinal);
    }

    private static OpenAIModelGateway Gateway(HttpClient client)
    {
        var route = NativeProviderTests.Route(false) with { Adapter = "OpenAIChatGPT", ChatGPTAccessToken = "own-plan-token" };
        return new OpenAIModelGateway(new ProviderHttpTransport(client, route, TimeProvider.System), route);
    }

    private static string Delta(string delta) => Event(new { type = "response.output_text.delta", output_index = 0, content_index = 0, delta });
    private static string Done(string text) => Event(new { type = "response.output_text.done", output_index = 0, content_index = 0, text });
    private static string ItemDone(string text) => Event(new
    {
        type = "response.output_item.done",
        output_index = 0,
        item = new { type = "message", content = new[] { new { type = "output_text", text } } }
    });
    private static string Completed(bool snapshot = false, string taskJson = TaskJson) => Event(new
    {
        type = "response.completed",
        response = new
        {
            status = "completed",
            output = snapshot ? new[] { new { type = "message", content = new[] { new { type = "output_text", text = taskJson } } } } : [],
            usage = new { input_tokens = 147, output_tokens = 43 }
        }
    });
    private static string Event(object value) => "data: " + JsonSerializer.Serialize(value, WireJson) + "\n\n";

    private sealed class StreamHandler(string events, string? mediaType = "text/event-stream", int chunk = 8192, byte[]? suffix = null) : HttpMessageHandler
    {
        public string? Accept { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Accept = request.Headers.Accept.Single().MediaType;
            var content = new StreamContent(new ChunkedStream([.. Encoding.UTF8.GetBytes(events), .. suffix ?? []], chunk));
            if (mediaType is not null) content.Headers.ContentType = new MediaTypeHeaderValue(mediaType);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
        }
    }

    private sealed class ChunkedStream(byte[] bytes, int chunk) : MemoryStream(bytes, false)
    {
        public override bool CanSeek => false;
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            base.ReadAsync(buffer[..Math.Min(buffer.Length, chunk)], cancellationToken);
    }
}
