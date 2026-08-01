using System.Text.Json;
using iRoute.Contracts;
using iRoute.Sdk.DotNet;

using var http = new HttpClient
{
    BaseAddress = new Uri(Environment.GetEnvironmentVariable("IROUTE_URL") ?? "http://localhost:8080")
};
var client = new IRouteClient(http, new IRouteClientOptions(
    TenantId: Environment.GetEnvironmentVariable("IROUTE_TENANT") ?? "demo",
    ActorId: Environment.GetEnvironmentVariable("IROUTE_ACTOR") ?? "sdk-example",
    BearerToken: Environment.GetEnvironmentVariable("IROUTE_TOKEN")));
using var input = JsonDocument.Parse("""{"purpose":"Confirm the SDK quick start."}""");
var result = await client.ExecuteAsync(new TaskRequest(
    "email.draft",
    input.RootElement.Clone(),
    IdempotencyKey: $"dotnet-example-{Guid.NewGuid():N}"));
Console.WriteLine(JsonSerializer.Serialize(result));
