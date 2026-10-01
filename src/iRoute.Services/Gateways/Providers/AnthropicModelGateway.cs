using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using iRoute.Common;

namespace iRoute.Services;

public sealed class AnthropicModelGateway(ProviderHttpTransport transport, ModelGatewayDeploymentOptions route) : IModelGateway
{
    public string GatewayId => route.GatewayId;

    public async Task<ModelGatewayResult> ExecuteAsync(ModelGatewayRequest request, CancellationToken cancellationToken)
    {
        var started = Stopwatch.StartNew();
        var body = new JsonObject
        {
            ["model"] = route.Model,
            ["system"] = ProviderTaskFormat.Instructions,
            ["max_tokens"] = request.MaxOutputTokens,
            ["messages"] = new JsonArray(new JsonObject { ["role"] = "user", ["content"] = ProviderTaskFormat.Prompt(request) }),
            ["output_config"] = new JsonObject
            {
                ["format"] = new JsonObject
                { ["type"] = "json_schema", ["schema"] = ProviderTaskFormat.Schema(request) }
            }
        };
        var response = await transport.SendAsync("messages", body, true, request, cancellationToken);
        try
        {
            if (response.GetProperty("stop_reason").GetString() != "end_turn")
                throw ProviderResponse.Invalid(request, "The Anthropic response did not complete (refused, tool call, or truncated).");
            var text = new StringBuilder();
            foreach (var content in response.GetProperty("content").EnumerateArray())
            {
                if (content.GetProperty("type").GetString() != "text")
                    throw ProviderResponse.Invalid(request, "The Anthropic response contained unsupported output.");
                text.Append(content.GetProperty("text").GetString());
            }
            var usage = response.GetProperty("usage");
            var input = ProviderResponse.AnthropicInputTokens(usage, request, out var cached);
            var outputTokens = ProviderResponse.Tokens(usage, "output_tokens", request);
            return ProviderResponse.Result(text.ToString(), input, outputTokens,
                request, route, started.ElapsedMilliseconds, cached,
                ProviderResponse.Detail(usage, "output_tokens_details", "thinking_tokens", outputTokens, request));
        }
        catch (Exception error) when (error is JsonException or KeyNotFoundException or InvalidOperationException or OverflowException)
        {
            throw ProviderResponse.Invalid(request, "The Anthropic response did not match the expected task contract.");
        }
    }
}
