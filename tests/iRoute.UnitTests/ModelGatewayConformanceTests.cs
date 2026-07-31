using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using iRoute.Contracts;
using iRoute.Core;
using iRoute.Infrastructure;
using Microsoft.Extensions.Options;

namespace iRoute.UnitTests;

public sealed class ModelGatewayConformanceTests
{
    [Fact]
    public async Task BufferedExternalGatewayUsesProviderNeutralContractAndNormalizesMetadata()
    {
        ModelGatewayRequest? capturedRequest = null;
        string? capturedAuthorization = null;
        string? capturedCorrelation = null;
        string? capturedDeadline = null;
        var expected = new ModelGatewayResult(
            JsonSerializer.SerializeToElement(new { subject = "Hello", body = "World" }),
            new UsageSummary(10, 4, 0.01m, ModelCalls: 1),
            0.91m,
            []);
        using var client = new HttpClient(new DelegateHandler(async (request, cancellationToken) =>
        {
            capturedAuthorization = request.Headers.Authorization?.ToString();
            capturedCorrelation = request.Headers.GetValues("X-Correlation-Id").Single();
            capturedDeadline = request.Headers.GetValues("X-Deadline-Milliseconds").Single();
            capturedRequest = await request.Content!.ReadFromJsonAsync<ModelGatewayRequest>(cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(expected) };
        }))
        {
            BaseAddress = new Uri("https://gateway.test/")
        };
        var gateway = new GenericHttpModelGateway(
            client,
            Options.Create(new ModelGatewayOptions
            {
                BaseUrl = "https://gateway.test/",
                ApiKey = "secret",
                GatewayId = "external-alpha",
                Transport = ModelGatewayTransport.Buffered
            }));
        var gatewayRequest = new ModelGatewayRequest(
            "text.generation",
            JsonSerializer.SerializeToElement(new { objective = "Test" }),
            JsonSerializer.SerializeToElement(new { context = "Known" }),
            400,
            "execution-123",
            "text.generation.small.eval-v1",
            5_000);

        var result = await gateway.ExecuteAsync(gatewayRequest, TestContext.Current.CancellationToken);

        Assert.Equal("Bearer secret", capturedAuthorization);
        Assert.Equal("execution-123", capturedCorrelation);
        Assert.Equal("5000", capturedDeadline);
        var captured = Assert.IsType<ModelGatewayRequest>(capturedRequest);
        Assert.Equal(gatewayRequest.Capability, captured.Capability);
        Assert.Equal(gatewayRequest.Input.GetRawText(), captured.Input.GetRawText());
        Assert.Equal(gatewayRequest.Context.GetRawText(), captured.Context.GetRawText());
        Assert.Equal(gatewayRequest.MaxOutputTokens, captured.MaxOutputTokens);
        Assert.Equal(gatewayRequest.CorrelationId, captured.CorrelationId);
        Assert.Equal(gatewayRequest.ProfileId, captured.ProfileId);
        Assert.Equal(gatewayRequest.DeadlineMilliseconds, captured.DeadlineMilliseconds);
        Assert.Equal(expected.Output.GetRawText(), result.Output.GetRawText());
        Assert.Equal(expected.Usage, result.Usage);
        Assert.Equal(expected.Confidence, result.Confidence);
        Assert.Empty(result.Evidence);
        Assert.Equal("external-alpha", result.GatewayId);
        Assert.Equal(ModelGatewayTransport.Buffered, result.Transport);
        Assert.Equal(ModelGatewayFinishReason.Completed, result.FinishReason);
    }

    [Fact]
    public async Task StreamingExternalGatewayUsesTheSameContractAndCompletesFromBoundedNdjson()
    {
        var expected = new ModelGatewayResult(
            JsonSerializer.SerializeToElement(new { subject = "Streamed", body = "Result" }),
            new UsageSummary(12, 5, 0.02m, 7),
            0.93m,
            []);
        var lines = new[]
        {
            JsonSerializer.Serialize(new ModelGatewayStreamEvent(
                1,
                ModelGatewayStreamEventKind.OutputDelta,
                Delta: "{\"subject\":"), JsonOptions),
            JsonSerializer.Serialize(new ModelGatewayStreamEvent(
                2,
                ModelGatewayStreamEventKind.Usage,
                Usage: expected.Usage), JsonOptions),
            JsonSerializer.Serialize(new ModelGatewayStreamEvent(
                3,
                ModelGatewayStreamEventKind.Completed,
                Result: expected), JsonOptions)
        };
        using var client = new HttpClient(new DelegateHandler((request, _) =>
        {
            Assert.Equal("/v1/stream", request.RequestUri!.AbsolutePath);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    string.Join('\n', lines) + "\n",
                    Encoding.UTF8,
                    "application/x-ndjson")
            });
        }))
        {
            BaseAddress = new Uri("https://gateway.test/")
        };
        var gateway = new GenericHttpModelGateway(
            client,
            Options.Create(new ModelGatewayOptions
            {
                BaseUrl = "https://gateway.test/",
                GatewayId = "external-beta",
                Transport = ModelGatewayTransport.Streaming
            }));
        var request = GatewayRequest("stream-correlation");
        var events = new List<ModelGatewayStreamEvent>();

        await foreach (var streamEvent in gateway.StreamAsync(
            request,
            TestContext.Current.CancellationToken))
        {
            events.Add(streamEvent);
        }

        Assert.Equal(3, events.Count);
        Assert.Equal(ModelGatewayStreamEventKind.OutputDelta, events[0].Kind);
        Assert.Equal(ModelGatewayStreamEventKind.Usage, events[1].Kind);
        var result = Assert.IsType<ModelGatewayResult>(events[2].Result);
        Assert.Equal(expected.Output.GetRawText(), result.Output.GetRawText());
        Assert.Equal("external-beta", result.GatewayId);
        Assert.Equal(ModelGatewayTransport.Streaming, result.Transport);
        Assert.Equal(1, result.Usage.ModelCalls);
    }

    [Fact]
    public async Task StreamingGatewayFailsClosedOnSequenceCompletionAndLineBounds()
    {
        var result = new ModelGatewayResult(
            JsonSerializer.SerializeToElement(new { subject = "Complete", body = "Result" }),
            new UsageSummary(1, 1, 0m, ModelCalls: 1),
            0.9m,
            []);
        var cases = new[]
        {
            string.Join('\n',
                JsonSerializer.Serialize(new ModelGatewayStreamEvent(
                    1,
                    ModelGatewayStreamEventKind.OutputDelta,
                    Delta: "first"), JsonOptions),
                JsonSerializer.Serialize(new ModelGatewayStreamEvent(
                    1,
                    ModelGatewayStreamEventKind.Completed,
                    Result: result), JsonOptions)),
            JsonSerializer.Serialize(new ModelGatewayStreamEvent(
                1,
                ModelGatewayStreamEventKind.OutputDelta,
                Delta: "missing completion"), JsonOptions),
            string.Join('\n',
                JsonSerializer.Serialize(new ModelGatewayStreamEvent(
                    1,
                    ModelGatewayStreamEventKind.Completed,
                    Result: result), JsonOptions),
                JsonSerializer.Serialize(new ModelGatewayStreamEvent(
                    2,
                    ModelGatewayStreamEventKind.OutputDelta,
                    Delta: "after completion"), JsonOptions)),
            new string('x', 65_537)
        };

        foreach (var content in cases)
        {
            using var client = new HttpClient(new DelegateHandler((_, _) =>
                Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        content + "\n",
                        Encoding.UTF8,
                        "application/x-ndjson")
                })))
            {
                BaseAddress = new Uri("https://gateway.test/")
            };
            var gateway = new GenericHttpModelGateway(
                client,
                Options.Create(new ModelGatewayOptions
                {
                    BaseUrl = "https://gateway.test/",
                    Transport = ModelGatewayTransport.Streaming
                }));

            var exception = await Assert.ThrowsAsync<ModelGatewayException>(() =>
                gateway.ExecuteAsync(GatewayRequest("invalid-stream"), TestContext.Current.CancellationToken));

            Assert.Equal(ErrorCodes.ModelGatewayInvalidResponse, exception.Code);
            Assert.Equal(ModelGatewayFailureKind.InvalidResponse, exception.FailureKind);
        }
    }

    [Fact]
    public async Task ExternalGatewayHealthIsNormalizedToTheConfiguredIdentity()
    {
        using var client = new HttpClient(new DelegateHandler((request, _) =>
        {
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Equal("/health", request.RequestUri!.AbsolutePath);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(new ModelGatewayHealth(
                    "provider-owned-name",
                    ModelGatewayHealthStatus.Healthy,
                    999,
                    DateTimeOffset.UnixEpoch,
                    "ready"))
            });
        }))
        {
            BaseAddress = new Uri("https://gateway.test/")
        };
        var gateway = new GenericHttpModelGateway(
            client,
            Options.Create(new ModelGatewayOptions
            {
                BaseUrl = "https://gateway.test/",
                GatewayId = "external-alpha"
            }));

        var health = await gateway.CheckHealthAsync(TestContext.Current.CancellationToken);

        Assert.Equal("external-alpha", health.GatewayId);
        Assert.Equal(ModelGatewayHealthStatus.Healthy, health.Status);
        Assert.True(health.LatencyMilliseconds >= 0);
        Assert.NotEqual(DateTimeOffset.UnixEpoch, health.CheckedAt);
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest, false, ModelGatewayFailureKind.InvalidRequest)]
    [InlineData(HttpStatusCode.Unauthorized, false, ModelGatewayFailureKind.Authentication)]
    [InlineData(HttpStatusCode.RequestTimeout, true, ModelGatewayFailureKind.Timeout)]
    [InlineData(HttpStatusCode.TooManyRequests, true, ModelGatewayFailureKind.RateLimited)]
    [InlineData(HttpStatusCode.ServiceUnavailable, true, ModelGatewayFailureKind.Unavailable)]
    public async Task HttpGatewayClassifiesHttpFailures(
        HttpStatusCode statusCode,
        bool retryable,
        ModelGatewayFailureKind failureKind)
    {
        using var client = new HttpClient(new DelegateHandler((_, _) =>
            Task.FromResult(new HttpResponseMessage(statusCode))))
        {
            BaseAddress = new Uri("https://gateway.test/")
        };
        var gateway = new GenericHttpModelGateway(
            client,
            Options.Create(new ModelGatewayOptions
            {
                BaseUrl = "https://gateway.test/",
                GatewayId = "external-failure"
            }));
        var request = GatewayRequest("failure-correlation");

        var exception = await Assert.ThrowsAsync<ModelGatewayException>(() =>
            gateway.ExecuteAsync(request, TestContext.Current.CancellationToken));

        Assert.Equal("model_gateway_http_error", exception.Code);
        Assert.Equal((int)statusCode, exception.StatusCode);
        Assert.Equal(retryable, exception.Retryable);
        Assert.Equal(failureKind, exception.FailureKind);
        Assert.Equal("external-failure", exception.GatewayId);
        Assert.Equal("failure-correlation", exception.CorrelationId);
    }

    [Fact]
    public async Task CallerCancellationPropagatesToTheExternalGatewayTransport()
    {
        var transportCancelled = false;
        using var client = new HttpClient(new DelegateHandler(async (_, cancellationToken) =>
        {
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                return new HttpResponseMessage(HttpStatusCode.OK);
            }
            catch (OperationCanceledException)
            {
                transportCancelled = true;
                throw;
            }
        }))
        {
            BaseAddress = new Uri("https://gateway.test/")
        };
        var gateway = new GenericHttpModelGateway(
            client,
            Options.Create(new ModelGatewayOptions { BaseUrl = "https://gateway.test/" }));
        using var cancellation = new CancellationTokenSource();
        cancellation.CancelAfter(TimeSpan.FromMilliseconds(25));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            gateway.ExecuteAsync(GatewayRequest("cancelled"), cancellation.Token));

        Assert.True(transportCancelled);
    }

    [Fact]
    public async Task CallerCancellationInterruptsAnActiveGatewayStreamRead()
    {
        using var stream = new CancellationObservingStream();
        using var client = new HttpClient(new DelegateHandler((_, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StreamContent(stream)
            })))
        {
            BaseAddress = new Uri("https://gateway.test/")
        };
        var gateway = new GenericHttpModelGateway(
            client,
            Options.Create(new ModelGatewayOptions
            {
                BaseUrl = "https://gateway.test/",
                Transport = ModelGatewayTransport.Streaming
            }));
        using var cancellation = new CancellationTokenSource();
        cancellation.CancelAfter(TimeSpan.FromMilliseconds(25));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            gateway.ExecuteAsync(GatewayRequest("cancelled-stream"), cancellation.Token));

        Assert.True(stream.CancellationObserved);
    }

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private static ModelGatewayRequest GatewayRequest(string correlationId) => new(
        "text.generation",
        JsonSerializer.SerializeToElement(new { objective = "Test" }),
        JsonSerializer.SerializeToElement(new { context = "Known" }),
        400,
        correlationId,
        "text.generation.small.eval-v1",
        5_000);

    private sealed class DelegateHandler(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) => handler(request, cancellationToken);
    }

    private sealed class CancellationObservingStream : Stream
    {
        public bool CancellationObserved { get; private set; }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush() => throw new NotSupportedException();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                return 0;
            }
            catch (OperationCanceledException)
            {
                CancellationObserved = true;
                throw;
            }
        }
    }
}
