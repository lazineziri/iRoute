using System.Text.Json;
using iRoute.Common;

namespace iRoute.Services;

public sealed class DeterministicModelGateway(TimeProvider clock) : IModelGateway
{
    public string GatewayId => "deterministic-development";

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
            [],
            "deterministic-development",
            ModelGatewayTransport.Buffered));
    }

    public Task<ModelGatewayHealth> CheckHealthAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(new ModelGatewayHealth(
            "deterministic-development",
            ModelGatewayHealthStatus.Healthy,
            0,
            clock.GetUtcNow(),
            "The deterministic development gateway is available."));
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
