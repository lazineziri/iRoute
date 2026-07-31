using System.Net.Http.Json;
using System.Text.Json;
using iRoute.Contracts;
using iRoute.Core;
using Microsoft.Extensions.Options;

namespace iRoute.Infrastructure;

public sealed class SystemClock : IClock
{
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
}

public sealed class BuiltInTaskDefinitionRegistry : ITaskDefinitionRegistry
{
    private static readonly IReadOnlyDictionary<string, TaskDefinition> Definitions =
        new Dictionary<string, TaskDefinition>(StringComparer.OrdinalIgnoreCase)
        {
            ["email.draft"] = new(
                "email.draft", 1, "text.generation", 800, 0.80m, true, SideEffectClass.None, "email.draft", 4000, 30000, 1),
            ["email.send"] = new(
                "email.send", 1, "email.send", 800, 0.90m, true, SideEffectClass.IrreversibleWrite, "email.send.receipt", 4000, 30000, 0),
            ["calendar.find_slots"] = new(
                "calendar.find_slots", 1, "calendar.read", 400, 0.95m, true, SideEffectClass.ReadOnly, "calendar.slot-proposal", 3000, 20000, 0),
            ["database.answer"] = new(
                "database.answer", 1, "database.read", 600, 0.95m, true, SideEffectClass.ReadOnly, "database.answer", 3000, 20000, 0),
            ["document.summarize"] = new(
                "document.summarize", 1, "text.summarization", 1200, 0.85m, true, SideEffectClass.None, "document.summary", 8000, 45000, 1)
        };

    public Task<TaskDefinition?> FindAsync(string taskType, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(Definitions.GetValueOrDefault(taskType));
    }
}

public sealed record ModelGatewayOptions
{
    public string Mode { get; init; } = "Deterministic";
    public string? BaseUrl { get; init; }
    public string? ApiKey { get; init; }
}

public sealed class DeterministicModelGateway : IModelGateway
{
    public Task<ModelGatewayResult> ExecuteAsync(
        ModelGatewayRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!string.Equals(request.Capability, "text.generation", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"The deterministic developer gateway does not implement capability '{request.Capability}'.");
        }

        var recipientName = ReadNestedString(request.Input, "recipient", "name")
            ?? ReadString(request.Input, "recipientName")
            ?? "there";
        var projectName = ReadString(request.Input, "projectName") ?? "the project";
        var objective = ReadString(request.Input, "objective")
            ?? "I wanted to share a concise project update.";
        var tone = ReadString(request.Input, "tone") ?? "professional";
        var contextSummary = BuildContextSummary(request.Context);
        var subject = $"Update on {projectName}";
        var body = $"Hi {recipientName},\n\n{objective.Trim()}" +
            (string.IsNullOrWhiteSpace(contextSummary) ? string.Empty : $"\n\n{contextSummary}") +
            $"\n\nBest regards";
        var output = JsonSerializer.SerializeToElement(new
        {
            subject,
            body,
            tone,
            generatedBy = "iroute-deterministic-development-gateway"
        });
        var inputTokens = EstimateTokens(request.Input) + EstimateTokens(request.Context);
        var outputTokens = EstimateTokens(output);
        return Task.FromResult(new ModelGatewayResult(
            output,
            new UsageSummary(inputTokens, outputTokens, 0m, ModelCalls: 1),
            0.92m,
            []));
    }

    private static string? ReadString(JsonElement element, string propertyName) =>
        element.ValueKind == JsonValueKind.Object &&
        element.TryGetProperty(propertyName, out var value) &&
        value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static string? ReadNestedString(JsonElement element, string objectName, string propertyName) =>
        element.ValueKind == JsonValueKind.Object &&
        element.TryGetProperty(objectName, out var nested) &&
        nested.ValueKind == JsonValueKind.Object
            ? ReadString(nested, propertyName)
            : null;

    private static string BuildContextSummary(JsonElement context)
    {
        if (context.ValueKind != JsonValueKind.Object)
        {
            return string.Empty;
        }

        var lines = new List<string>();
        AppendContextLines(context, "activeDecisions", "Active decision", lines);
        AppendContextLines(context, "projectHistory", "Project context", lines);
        return string.Join("\n", lines.Take(6));
    }

    private static void AppendContextLines(
        JsonElement context,
        string propertyName,
        string label,
        List<string> lines)
    {
        if (!context.TryGetProperty(propertyName, out var values) || values.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        foreach (var value in values.EnumerateArray())
        {
            var text = value.ValueKind switch
            {
                JsonValueKind.String => value.GetString(),
                JsonValueKind.Object => ReadString(value, "text") ?? ReadString(value, "value") ?? ReadString(value, "summary"),
                _ => null
            };
            if (!string.IsNullOrWhiteSpace(text))
            {
                lines.Add($"{label}: {text.Trim()}");
            }
        }
    }

    private static int EstimateTokens(JsonElement value) =>
        Math.Max(1, (int)Math.Ceiling(value.GetRawText().Length / 4d));
}

public sealed class GenericHttpModelGateway(HttpClient httpClient, IOptions<ModelGatewayOptions> options) : IModelGateway
{
    public async Task<ModelGatewayResult> ExecuteAsync(ModelGatewayRequest request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(options.Value.BaseUrl))
        {
            throw new InvalidOperationException("ModelGateway:BaseUrl is not configured.");
        }

        using var message = new HttpRequestMessage(HttpMethod.Post, "v1/execute")
        {
            Content = JsonContent.Create(request)
        };
        if (!string.IsNullOrWhiteSpace(options.Value.ApiKey))
        {
            message.Headers.Authorization = new("Bearer", options.Value.ApiKey);
        }

        if (!string.IsNullOrWhiteSpace(request.CorrelationId))
        {
            message.Headers.TryAddWithoutValidation("X-Correlation-Id", request.CorrelationId);
        }

        HttpResponseMessage response;
        try
        {
            response = await httpClient.SendAsync(message, cancellationToken);
        }
        catch (HttpRequestException exception)
        {
            throw new ModelGatewayException(
                ErrorCodes.ModelGatewayUnavailable,
                "The configured model gateway could not be reached.",
                true,
                innerException: exception);
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                var statusCode = (int)response.StatusCode;
                var retryable = statusCode is 408 or 429 || statusCode >= 500;
                throw new ModelGatewayException(
                    ErrorCodes.ModelGatewayHttpError,
                    $"The configured model gateway returned HTTP {statusCode}.",
                    retryable,
                    statusCode);
            }

            try
            {
                return await response.Content.ReadFromJsonAsync<ModelGatewayResult>(cancellationToken)
                    ?? throw new ModelGatewayException(
                        ErrorCodes.ModelGatewayInvalidResponse,
                        "The configured model gateway returned an empty response.",
                        false);
            }
            catch (JsonException exception)
            {
                throw new ModelGatewayException(
                    ErrorCodes.ModelGatewayInvalidResponse,
                    "The configured model gateway returned invalid JSON.",
                    false,
                    innerException: exception);
            }
        }
    }
}
