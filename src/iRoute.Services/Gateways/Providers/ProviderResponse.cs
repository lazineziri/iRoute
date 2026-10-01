using System.Text.Json;
using iRoute.Common;

namespace iRoute.Services;

internal static class ProviderResponse
{
    internal static ModelGatewayException Invalid(ModelGatewayRequest request, string message) => new(
        ErrorCodes.ModelGatewayInvalidResponse, message, false,
        failureKind: ModelGatewayFailureKind.InvalidResponse,
        correlationId: request.CorrelationId, failureClass: GatewayFailureClass.MalformedOutput);

    internal static int Tokens(JsonElement usage, string property, ModelGatewayRequest request)
    {
        if (!usage.TryGetProperty(property, out var count) || count.ValueKind != JsonValueKind.Number ||
            !count.TryGetInt32(out var tokens) || tokens < 0)
            throw Invalid(request, "The provider returned missing or invalid token usage.");
        return tokens;
    }

    internal static int AnthropicInputTokens(JsonElement usage, ModelGatewayRequest request, out int? cached)
    {
        cached = usage.TryGetProperty("cache_read_input_tokens", out var read) && read.ValueKind != JsonValueKind.Null
            ? Tokens(usage, "cache_read_input_tokens", request) : null;
        var written = usage.TryGetProperty("cache_creation_input_tokens", out var created) && created.ValueKind != JsonValueKind.Null
            ? Tokens(usage, "cache_creation_input_tokens", request) : 0;
        return checked(Tokens(usage, "input_tokens", request) + (cached ?? 0) + written);
    }

    internal static ModelGatewayResult Result(string text, int inputTokens, int outputTokens,
        ModelGatewayRequest request, ModelGatewayDeploymentOptions route, long duration,
        int? cachedInputTokens = null, int? reasoningTokens = null)
    {
        var costKnown = !ModelGatewayModes.IsSubscription(route.Adapter) &&
            route.InputCostPerMillionTokens.HasValue && route.OutputCostPerMillionTokens.HasValue;
        var cost = (inputTokens * (route.InputCostPerMillionTokens ?? 0m) +
                    outputTokens * (route.OutputCostPerMillionTokens ?? 0m)) / 1_000_000m;
        var reportedUsage = new UsageSummary(inputTokens, outputTokens, costKnown ? cost : 0m, duration, ModelCalls: 1,
            CostKnown: costKnown, CachedInputTokens: cachedInputTokens, ReasoningTokens: reasoningTokens);
        if (outputTokens > request.MaxOutputTokens)
            throw new ModelGatewayException(ErrorCodes.ModelGatewayInvalidResponse, "The provider exceeded the task output-token limit.",
                false, failureKind: ModelGatewayFailureKind.InvalidResponse, correlationId: request.CorrelationId,
                failureClass: GatewayFailureClass.MalformedOutput, reportedUsage: reportedUsage);
        using var output = JsonDocument.Parse(text);
        if (output.RootElement.ValueKind != JsonValueKind.Object)
            throw Invalid(request, "The provider did not return a JSON task object.");
        var schema = ProviderTaskFormat.Schema(request);
        var properties = schema["properties"]!.AsObject();
        foreach (var field in schema["required"]!.AsArray())
        {
            var name = field!.GetValue<string>();
            if (!output.RootElement.TryGetProperty(name, out var value) ||
                !MatchesType(value, properties[name]!["type"]!.GetValue<string>()))
                throw Invalid(request, "The provider output has a missing or incorrectly typed task field.");
        }
        if (output.RootElement.EnumerateObject().Any(field => !properties.ContainsKey(field.Name)))
            throw Invalid(request, "The provider output contains unexpected task fields.");

        return new ModelGatewayResult(output.RootElement.Clone(),
            reportedUsage,
            route.ExpectedQuality, [], route.GatewayId);
    }

    internal static int? Detail(JsonElement usage, string details, string property, int total, ModelGatewayRequest request)
    {
        if (!usage.TryGetProperty(details, out var value) || value.ValueKind == JsonValueKind.Null) return null;
        if (!value.TryGetProperty(property, out _)) return null;
        var count = Tokens(value, property, request);
        if (count > total) throw Invalid(request, "The provider returned inconsistent token usage details.");
        return count;
    }

    private static bool MatchesType(JsonElement value, string type) => type switch
    {
        "string" => value.ValueKind == JsonValueKind.String,
        "array" => value.ValueKind == JsonValueKind.Array &&
            value.EnumerateArray().All(item => item.ValueKind == JsonValueKind.String),
        _ => false
    };
}
