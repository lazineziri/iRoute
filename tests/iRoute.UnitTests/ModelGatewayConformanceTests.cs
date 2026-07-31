using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using iRoute.Contracts;
using iRoute.Core;
using iRoute.Infrastructure;
using Microsoft.Extensions.Options;

namespace iRoute.UnitTests;

public sealed class ModelGatewayConformanceTests
{
    [Fact]
    public async Task HttpGatewayUsesProviderNeutralContractAndScopeHeaders()
    {
        ModelGatewayRequest? capturedRequest = null;
        string? capturedAuthorization = null;
        string? capturedCorrelation = null;
        var expected = new ModelGatewayResult(
            JsonSerializer.SerializeToElement(new { subject = "Hello", body = "World" }),
            new UsageSummary(10, 4, 0.01m, ModelCalls: 1),
            0.91m,
            []);
        using var client = new HttpClient(new DelegateHandler(async (request, cancellationToken) =>
        {
            capturedAuthorization = request.Headers.Authorization?.ToString();
            capturedCorrelation = request.Headers.GetValues("X-Correlation-Id").Single();
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
                ApiKey = "secret"
            }));
        var gatewayRequest = new ModelGatewayRequest(
            "text.generation",
            JsonSerializer.SerializeToElement(new { objective = "Test" }),
            JsonSerializer.SerializeToElement(new { context = "Known" }),
            400,
            "execution-123");

        var result = await gateway.ExecuteAsync(gatewayRequest, TestContext.Current.CancellationToken);

        Assert.Equal("Bearer secret", capturedAuthorization);
        Assert.Equal("execution-123", capturedCorrelation);
        var captured = Assert.IsType<ModelGatewayRequest>(capturedRequest);
        Assert.Equal(gatewayRequest.Capability, captured.Capability);
        Assert.Equal(gatewayRequest.Input.GetRawText(), captured.Input.GetRawText());
        Assert.Equal(gatewayRequest.Context.GetRawText(), captured.Context.GetRawText());
        Assert.Equal(gatewayRequest.MaxOutputTokens, captured.MaxOutputTokens);
        Assert.Equal(gatewayRequest.CorrelationId, captured.CorrelationId);
        Assert.Equal(expected.Output.GetRawText(), result.Output.GetRawText());
        Assert.Equal(expected.Usage, result.Usage);
        Assert.Equal(expected.Confidence, result.Confidence);
        Assert.Empty(result.Evidence);
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest, false)]
    [InlineData(HttpStatusCode.RequestTimeout, true)]
    [InlineData(HttpStatusCode.TooManyRequests, true)]
    [InlineData(HttpStatusCode.ServiceUnavailable, true)]
    public async Task HttpGatewayClassifiesHttpFailures(HttpStatusCode statusCode, bool retryable)
    {
        using var client = new HttpClient(new DelegateHandler((_, _) =>
            Task.FromResult(new HttpResponseMessage(statusCode))))
        {
            BaseAddress = new Uri("https://gateway.test/")
        };
        var gateway = new GenericHttpModelGateway(
            client,
            Options.Create(new ModelGatewayOptions { BaseUrl = "https://gateway.test/" }));
        var request = new ModelGatewayRequest(
            "text.generation",
            JsonSerializer.SerializeToElement(new { }),
            JsonSerializer.SerializeToElement(new { }),
            100);

        var exception = await Assert.ThrowsAsync<ModelGatewayException>(() =>
            gateway.ExecuteAsync(request, TestContext.Current.CancellationToken));

        Assert.Equal("model_gateway_http_error", exception.Code);
        Assert.Equal((int)statusCode, exception.StatusCode);
        Assert.Equal(retryable, exception.Retryable);
    }

    private sealed class DelegateHandler(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) => handler(request, cancellationToken);
    }
}
