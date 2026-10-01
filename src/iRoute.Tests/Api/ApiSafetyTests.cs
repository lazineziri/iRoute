using System.Net;
using System.Net.Http.Headers;
using System.Text;
using iRoute.Runtime;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Xunit;

namespace iRoute.Tests.Api;

public sealed class ApiSafetyTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task OversizedBodiesAreRejectedBeforeExecution()
    {
        await using var host = await StartAsync(100);
        using var body = new StringContent(new string('x', 1025), Encoding.UTF8, "application/json");
        using var response = await host.Client.PostAsync("/v1/executions/", body, Token);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        Assert.Contains("Request body limit exceeded", await response.Content.ReadAsStringAsync(Token), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ChunkedRequestsCannotBypassTheBodyLimit()
    {
        await using var host = await StartAsync(100);
        using var body = new ChunkedBody();
        using var response = await host.Client.PostAsync("/v1/executions/", body, Token);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
    }

    [Fact]
    public async Task ChangingUntrustedTenantHeadersCannotEscapeRateLimitsAndHealthRemainsAvailable()
    {
        await using var host = await StartAsync(2);
        for (var index = 0; index < 3; index++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, "/v1/executions/" + Guid.NewGuid());
            request.Headers.Add("X-Tenant-Id", "forged-" + index);
            using var response = await host.Client.SendAsync(request, Token);
            Assert.Equal(index < 2 ? HttpStatusCode.NotFound : HttpStatusCode.TooManyRequests, response.StatusCode);
            if (index == 2) Assert.True(response.Headers.RetryAfter!.Delta!.Value > TimeSpan.Zero);
        }
        using var health = await host.Client.GetAsync("/health/live", Token);
        Assert.Equal(HttpStatusCode.OK, health.StatusCode);
    }

    private static async Task<TestHost> StartAsync(int limit)
    {
        var directory = Directory.CreateTempSubdirectory("iroute-api-test-");
        var app = ApiHost.Create([], builder =>
        {
            builder.Environment.EnvironmentName = "Development";
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            builder.Logging.ClearProviders();
            builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Storage:Provider"] = "Sqlite",
                ["Storage:AutoInitialize"] = "true",
                ["ConnectionStrings:iRoute"] = $"Data Source={Path.Combine(directory.FullName, "api.db")};Pooling=False",
                ["ModelGateway:Mode"] = "Deterministic",
                ["Runtime:RunBackgroundWorkers"] = "false",
                ["Identity:Mode"] = "DevelopmentHeaders",
                ["ApiLimits:MaxBodyBytes"] = "1024",
                ["ApiLimits:RequestsPerMinute"] = limit.ToString(System.Globalization.CultureInfo.InvariantCulture)
            });
        });
        try
        {
            await app.StartAsync(Token);
            return new(app, new HttpClient { BaseAddress = new Uri(app.Urls.Single()), Timeout = TimeSpan.FromSeconds(10) }, directory);
        }
        catch
        {
            await app.DisposeAsync();
            directory.Delete(true);
            throw;
        }
    }

    private sealed record TestHost(WebApplication App, HttpClient Client, DirectoryInfo Directory) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await App.StopAsync(Token);
            await App.DisposeAsync();
            Directory.Delete(true);
        }
    }

    private sealed class ChunkedBody : HttpContent
    {
        public ChunkedBody() => Headers.ContentType = new MediaTypeHeaderValue("application/json");
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
            stream.WriteAsync(Encoding.UTF8.GetBytes("{\"taskType\":\"email.draft\",\"input\":{\"objective\":\"" + new string('x', 4096) + "\"}}")).AsTask();
        protected override bool TryComputeLength(out long length) { length = 0; return false; }
    }
}
