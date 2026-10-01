using System.Text.Json;
using iRoute.Common;
using Xunit;

namespace iRoute.Tests.Contracts;

public sealed class GatewayContractTests
{
    [Fact]
    public void LegacyUsageDefaultsToKnownWhileNewUnknownCostIsExplicit()
    {
        var options = JsonSerializerOptions.Web;
        var old = JsonSerializer.Deserialize<UsageSummary>("{\"cost\":0.25}", options)!;
        Assert.True(old.CostKnown);
        var current = JsonSerializer.SerializeToElement(old with { CostKnown = false }, options);
        Assert.False(current.GetProperty("costKnown").GetBoolean());
        var request = JsonSerializer.Deserialize<ModelGatewayRequest>(
            "{\"capability\":\"text.generation\",\"input\":{},\"context\":{},\"maxOutputTokens\":100}", options)!;
        Assert.Equal("local", request.TenantId);
    }

    [Theory]
    [InlineData("task-outcome")]
    [InlineData("artifact-snapshot")]
    [InlineData("model-gateway-request")]
    [InlineData("model-gateway-result")]
    [InlineData("model-gateway-stream-event")]
    [InlineData("capability-invocation-result")]
    public async Task GatewaySchemaPropertiesMatchTheirCompatibilitySnapshot(string name)
    {
        using var schema = JsonDocument.Parse(await File.ReadAllTextAsync(
            Path.Combine(AppContext.BaseDirectory, "spec", "schemas", name + ".schema.json"), TestContext.Current.CancellationToken));
        using var snapshot = JsonDocument.Parse(await File.ReadAllTextAsync(
            Path.Combine(AppContext.BaseDirectory, "spec", "public-contract.snapshot.json"), TestContext.Current.CancellationToken));
        var expected = snapshot.RootElement.GetProperty("jsonPropertySchemas").GetProperty(schema.RootElement.GetProperty("$id").GetString()!);
        Assert.True(JsonElement.DeepEquals(schema.RootElement.GetProperty("properties"), expected));
    }
}
