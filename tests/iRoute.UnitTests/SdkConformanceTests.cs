using System.Net;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using iRoute.Contracts;
using iRoute.Sdk.DotNet;

namespace iRoute.UnitTests;

public sealed class SdkConformanceTests
{
    private static readonly Dictionary<string, string> Fixture = LoadFixture();
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task DotNetSdkMatchesSharedRequestFixture()
    {
        RequestCapture? captured = null;
        using var client = CreateHttpClient(async request =>
        {
            captured = new RequestCapture(
                request.Method.Method,
                request.RequestUri?.PathAndQuery ?? string.Empty,
                request.Headers.ToDictionary(item => item.Key, item => string.Join(' ', item.Value), StringComparer.OrdinalIgnoreCase),
                request.Content?.Headers.ContentType?.MediaType ?? string.Empty,
                request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync());
            return JsonResponse(HttpStatusCode.OK, Decode("response.body.base64"));
        });
        var sdk = CreateSdk(client);
        var request = JsonSerializer.Deserialize<TaskRequest>(Decode("request.body.base64"), JsonOptions)
            ?? throw new InvalidOperationException("The shared request fixture is invalid.");

        var result = await sdk.ExecuteAsync(request, TestContext.Current.CancellationToken);

        Assert.NotNull(captured);
        Assert.Equal(Fixture["request.method"], captured.Method);
        Assert.Equal(Fixture["request.path"], captured.Path);
        Assert.Equal(Fixture["request.header.authorization"], captured.Headers["Authorization"]);
        Assert.Equal(Fixture["request.header.tenant"], captured.Headers["X-Tenant-Id"]);
        Assert.Equal(Fixture["request.header.actor"], captured.Headers["X-Actor-Id"]);
        Assert.Equal(Fixture["request.header.permission-scopes"], captured.Headers["X-Permission-Scopes"]);
        Assert.Equal(Fixture["request.header.idempotency-key"], captured.Headers["Idempotency-Key"]);
        Assert.Equal(Fixture["request.header.content-type"], captured.ContentType);
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(Decode("request.body.base64")), JsonNode.Parse(captured.Body)));
        Assert.Equal(Guid.Parse("019fbc00-0000-7000-8000-000000000014"), result.ExecutionId);
    }

    [Fact]
    public async Task DotNetSdkMatchesSharedStreamFixture()
    {
        using var client = CreateHttpClient(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(Decode("stream.body.base64"), Encoding.UTF8, Fixture["stream.content-type"])
        }));
        var sdk = CreateSdk(client);
        var events = new List<ExecutionEvent>();

        await foreach (var executionEvent in sdk.StreamEventsAsync(
                           Guid.Parse("019fbc00-0000-7000-8000-000000000014"),
                           cancellationToken: TestContext.Current.CancellationToken))
        {
            events.Add(executionEvent);
        }

        Assert.Equal(int.Parse(Fixture["stream.expected.count"], CultureInfo.InvariantCulture), events.Count);
        Assert.Equal(Fixture["stream.expected.types"].Split(','), events.Select(item => item.Type));
    }

    [Fact]
    public async Task DotNetSdkMatchesSharedErrorFixture()
    {
        using var client = CreateHttpClient(_ => Task.FromResult(JsonResponse(
            (HttpStatusCode)int.Parse(Fixture["error.status"], CultureInfo.InvariantCulture),
            Decode("error.body.base64"))));
        var sdk = CreateSdk(client);
        var request = JsonSerializer.Deserialize<TaskRequest>(Decode("request.body.base64"), JsonOptions)
            ?? throw new InvalidOperationException("The shared request fixture is invalid.");

        var error = await Assert.ThrowsAsync<IRouteApiException>(() =>
            sdk.ExecuteAsync(request, TestContext.Current.CancellationToken));

        Assert.Equal(
            (HttpStatusCode)int.Parse(Fixture["error.status"], CultureInfo.InvariantCulture),
            error.StatusCode);
        Assert.Equal(Fixture["error.expected.code"], error.Code);
        Assert.Equal(Fixture["error.expected.title"], error.Title);
        Assert.Equal(Fixture["error.expected.detail"], error.Detail);
        Assert.Equal(Decode("error.body.base64"), error.ResponseBody);
    }

    private static IRouteClient CreateSdk(HttpClient client) => new(
        client,
        new IRouteClientOptions(
            TenantId: Fixture["request.header.tenant"],
            ActorId: Fixture["request.header.actor"],
            PermissionScopes: Fixture["request.header.permission-scopes"].Split(' '),
            BearerToken: "fixture-token"));

    private static HttpClient CreateHttpClient(
        Func<HttpRequestMessage, Task<HttpResponseMessage>> response) => new(new StubHandler(response))
        {
            BaseAddress = new Uri("http://sdk-fixture.test/")
        };

    private static HttpResponseMessage JsonResponse(HttpStatusCode status, string body) => new(status)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json")
    };

    private static string Decode(string key) =>
        Encoding.UTF8.GetString(Convert.FromBase64String(Fixture[key]));

    private static Dictionary<string, string> LoadFixture()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var path = Path.Combine(directory.FullName, "sdks", "conformance", "v1.properties");
            if (File.Exists(path))
            {
                return File.ReadLines(path)
                    .Where(line => line.Length > 0 && !line.StartsWith('#'))
                    .Select(line => line.Split('=', 2))
                    .ToDictionary(parts => parts[0], parts => parts[1], StringComparer.Ordinal);
            }

            directory = directory.Parent;
        }

        throw new FileNotFoundException("Could not find sdks/conformance/v1.properties.");
    }

    private sealed record RequestCapture(
        string Method,
        string Path,
        IReadOnlyDictionary<string, string> Headers,
        string ContentType,
        string Body);

    private sealed class StubHandler(
        Func<HttpRequestMessage, Task<HttpResponseMessage>> response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) => response(request);
    }
}
