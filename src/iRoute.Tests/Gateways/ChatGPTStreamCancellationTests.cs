using System.Net;
using System.Text;
using iRoute.Common;
using iRoute.Services;
using Xunit;

namespace iRoute.Tests.Gateways;

public sealed class ChatGPTStreamCancellationTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task DeadlineAlsoBoundsStreamReadsAfterResponseHeadersArrive()
    {
        using var handler = new WaitingStreamHandler();
        using var client = new HttpClient(handler);
        var error = await Assert.ThrowsAsync<ModelGatewayException>(() =>
            Gateway(client).ExecuteAsync(NativeProviderTests.Request() with { DeadlineMilliseconds = 100 }, Token));
        Assert.True(handler.Waiting.Task.IsCompleted);
        Assert.Equal(GatewayFailureClass.Timeout, error.FailureClass);
        Assert.True(error.Retryable);
    }

    [Fact]
    public async Task CallerCancellationDuringStreamingRemainsCancellation()
    {
        using var handler = new WaitingStreamHandler();
        using var client = new HttpClient(handler);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(Token);
        var pending = Gateway(client).ExecuteAsync(NativeProviderTests.Request(), cancellation.Token);
        await handler.Waiting.Task.WaitAsync(Token);
        await cancellation.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
    }

    private static OpenAIModelGateway Gateway(HttpClient client)
    {
        var route = NativeProviderTests.Route(false) with { Adapter = "OpenAIChatGPT", ChatGPTAccessToken = "own-plan-token" };
        return new OpenAIModelGateway(new ProviderHttpTransport(client, route, TimeProvider.System), route);
    }

    private sealed class WaitingStreamHandler : HttpMessageHandler
    {
        public TaskCompletionSource Waiting { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new WaitingStream(Waiting)) });
    }

    private sealed class WaitingStream(TaskCompletionSource waiting) : MemoryStream(Encoding.UTF8.GetBytes(": heartbeat\n\n"), false)
    {
        public override bool CanSeek => false;

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var count = await base.ReadAsync(buffer, cancellationToken);
            if (count != 0) return count;
            waiting.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException("The cancelled stream must not complete.");
        }
    }
}
