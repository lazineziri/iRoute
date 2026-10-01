using System.Net;
using System.Text;
using System.Text.Json;
using iRoute.Common;
using iRoute.Services;
using Xunit;

namespace iRoute.Tests.Gateways;

public sealed class ProviderUsageDetailTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(40, 5, true)]
    [InlineData(101, 5, false)]
    [InlineData(40, 21, false)]
    [InlineData(-1, 5, false)]
    public async Task TokenBreakdownsAreSubsetsNotExtraTokens(int cached, int reasoning, bool valid)
    {
        var response = JsonSerializer.Serialize(new
        {
            status = "completed",
            output = new[] { new { type = "message", content = new[] { new { type = "output_text", text = "{\"subject\":\"Review\",\"body\":\"Review this.\",\"tone\":\"professional\"}" } } } },
            usage = new
            {
                input_tokens = 100,
                output_tokens = 20,
                input_tokens_details = new { cached_tokens = cached },
                output_tokens_details = new { reasoning_tokens = reasoning }
            }
        });
        using var handler = new ResponseHandler(response);
        using var client = new HttpClient(handler);
        var route = NativeProviderTests.Route(false);
        var gateway = new OpenAIModelGateway(new ProviderHttpTransport(client, route, TimeProvider.System), route);
        if (!valid)
        {
            var error = await Assert.ThrowsAsync<ModelGatewayException>(() => gateway.ExecuteAsync(NativeProviderTests.Request(), Token));
            Assert.Equal(ErrorCodes.ModelGatewayInvalidResponse, error.Code);
            return;
        }
        var usage = (await gateway.ExecuteAsync(NativeProviderTests.Request(), Token)).Usage;
        Assert.Equal(120, usage.InputTokens + usage.OutputTokens);
        Assert.Equal(cached, usage.CachedInputTokens);
        Assert.Equal(reasoning, usage.ReasoningTokens);
    }

    private sealed class ResponseHandler(string response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(response, Encoding.UTF8, "application/json") });
    }
}
