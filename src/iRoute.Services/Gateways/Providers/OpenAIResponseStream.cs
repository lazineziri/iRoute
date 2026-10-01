using System.Text;
using System.Text.Json;
using iRoute.Common;

namespace iRoute.Services;

internal static class OpenAIResponseStream
{
    private const int MaximumBytes = 2 * 1024 * 1024;
    private static readonly UTF8Encoding Utf8 = new(false, true);

    public static async Task<JsonElement> ReadAsync(Stream stream, ModelGatewayRequest request, CancellationToken cancellationToken)
    {
        var bytes = new byte[MaximumBytes];
        var length = 0;
        var frameStart = 0;
        var lineStart = 0;
        var previousCarriageReturn = false;
        var output = new OpenAIStreamedText(request);
        while (length < MaximumBytes)
        {
            var count = await stream.ReadAsync(bytes.AsMemory(length, Math.Min(8192, MaximumBytes - length)), cancellationToken);
            if (count == 0) throw ProviderResponse.Invalid(request, "The ChatGPT stream ended without response.completed.");
            var end = length + count;
            for (var index = length; index < end; index++)
            {
                if (bytes[index] == (byte)'\n' && previousCarriageReturn)
                {
                    previousCarriageReturn = false;
                    lineStart = index + 1;
                    if (frameStart == index) frameStart = index + 1;
                    continue;
                }
                previousCarriageReturn = false;
                if (bytes[index] is not ((byte)'\n' or (byte)'\r')) continue;
                previousCarriageReturn = bytes[index] == (byte)'\r';
                var empty = index == lineStart;
                lineStart = index + 1;
                if (!empty) continue;
                string frame;
                try { frame = Utf8.GetString(bytes, frameStart, index - frameStart).Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n'); }
                catch (DecoderFallbackException) { throw ProviderResponse.Invalid(request, "The ChatGPT stream contained invalid UTF-8."); }
                frameStart = index + 1;
                var data = string.Join('\n', frame.Split('\n').Select(line => line.TrimEnd('\r'))
                    .Where(line => line.StartsWith("data:", StringComparison.Ordinal)).Select(line => line[5..].TrimStart(' ')));
                if (string.IsNullOrEmpty(data)) continue;
                if (data == "[DONE]") throw ProviderResponse.Invalid(request, "ChatGPT did not return a completed response.");
                var response = ReadFrame(data, output, request);
                if (response is { } completed) return completed;
            }
            length = end;
        }
        throw ProviderResponse.Invalid(request, "The ChatGPT event stream exceeded its safety limit.");
    }

    private static JsonElement? ReadFrame(string data, OpenAIStreamedText output, ModelGatewayRequest request)
    {
        try
        {
            using var document = JsonDocument.Parse(data);
            var value = document.RootElement;
            var type = value.GetProperty("type").GetString();
            if (type == "response.completed") return output.Complete(value.GetProperty("response"));
            if (type is "response.failed" or "response.incomplete" or "error") throw Failure(value, request);
            output.Observe(type, value);
            return null;
        }
        catch (Exception error) when (error is JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
        {
            throw ProviderResponse.Invalid(request, "The ChatGPT event stream did not match the expected response contract.");
        }
    }

    private static ModelGatewayException Failure(JsonElement value, ModelGatewayRequest request)
    {
        var error = value.TryGetProperty("response", out var response) && response.TryGetProperty("error", out var nested)
            ? nested : value;
        var code = error.ValueKind == JsonValueKind.Object && error.TryGetProperty("code", out var field) ? field.GetString() : null;
        return code switch
        {
            "subscription_sharing_usage_limit_exceeded" => new ModelGatewayException(ErrorCodes.ModelGatewayUnavailable,
                "ChatGPT plan/app usage limit reached. Review https://chatgpt.com/settings/usage; API billing was not used.", false,
                failureKind: ModelGatewayFailureKind.RateLimited, correlationId: request.CorrelationId, failureClass: GatewayFailureClass.Policy),
            "subscription_sharing_usage_unavailable" or "subscription_sharing_user_unavailable" => new ModelGatewayException(
                ErrorCodes.ModelGatewayUnavailable, "ChatGPT plan usage is temporarily unavailable.", true,
                failureKind: ModelGatewayFailureKind.Unavailable, correlationId: request.CorrelationId, failureClass: GatewayFailureClass.Provider),
            _ => ProviderResponse.Invalid(request, "The ChatGPT response failed, was incomplete, or was not authorized; no API billing fallback was used.")
        };
    }
}
