using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using iRoute.Common;

namespace iRoute.Services;

public sealed class OpenAIModelGateway(ProviderHttpTransport transport, ModelGatewayDeploymentOptions route) : IModelGateway
{
    public string GatewayId => route.GatewayId;

    public async Task<ModelGatewayResult> ExecuteAsync(ModelGatewayRequest request, CancellationToken cancellationToken)
    {
        var started = Stopwatch.StartNew();
        var body = new JsonObject
        {
            ["model"] = route.Model,
            ["instructions"] = ProviderTaskFormat.Instructions,
            ["input"] = ProviderTaskFormat.Prompt(request),
            ["store"] = false,
            ["text"] = new JsonObject
            {
                ["format"] = new JsonObject
                { ["type"] = "json_schema", ["name"] = "iroute_task", ["strict"] = true, ["schema"] = ProviderTaskFormat.Schema(request) }
            }
        };
        if (route.Adapter.Equals("OpenAIChatGPT", StringComparison.OrdinalIgnoreCase))
        {
            body["input"] = new JsonArray(new JsonObject { ["role"] = "user", ["content"] = ProviderTaskFormat.Prompt(request) });
            body["stream"] = true;
        }
        else body["max_output_tokens"] = request.MaxOutputTokens;
        if (!string.IsNullOrWhiteSpace(route.ReasoningEffort))
            body["reasoning"] = new JsonObject { ["effort"] = route.ReasoningEffort };
        var response = await transport.SendAsync("responses", body, false, request, cancellationToken);
        try
        {
            if (response.GetProperty("status").GetString() != "completed")
                throw ProviderResponse.Invalid(request, "The OpenAI response did not complete (refused, failed, or truncated).");
            var text = new StringBuilder();
            foreach (var output in response.GetProperty("output").EnumerateArray())
            {
                var type = output.GetProperty("type").GetString();
                if (type == "reasoning") continue;
                if (type != "message")
                    throw ProviderResponse.Invalid(request, "The OpenAI response contained unsupported output.");
                foreach (var content in output.GetProperty("content").EnumerateArray())
                {
                    if (content.GetProperty("type").GetString() != "output_text")
                        throw ProviderResponse.Invalid(request, "The OpenAI response contained a refusal or unsupported output.");
                    text.Append(content.GetProperty("text").GetString());
                }
            }
            var usage = response.GetProperty("usage");
            var inputTokens = ProviderResponse.Tokens(usage, "input_tokens", request);
            var outputTokens = ProviderResponse.Tokens(usage, "output_tokens", request);
            return ProviderResponse.Result(text.ToString(), inputTokens, outputTokens, request, route, started.ElapsedMilliseconds,
                ProviderResponse.Detail(usage, "input_tokens_details", "cached_tokens", inputTokens, request),
                ProviderResponse.Detail(usage, "output_tokens_details", "reasoning_tokens", outputTokens, request));
        }
        catch (Exception error) when (error is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            throw ProviderResponse.Invalid(request, "The OpenAI response did not match the expected task contract.");
        }
    }
}
