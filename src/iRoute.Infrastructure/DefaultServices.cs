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
                "email.draft", 1, "text.generation", 800, 0.80m, true, SideEffectClass.None, "email.draft",
                DefaultMaxInputTokens: 4000,
                DefaultDeadlineMilliseconds: 30000,
                DefaultMaxModelCalls: 1,
                AllowedCapabilities: ["text.generation"]),
            ["email.send"] = new(
                "email.send", 1, "email.send", 800, 0.90m, true, SideEffectClass.IrreversibleWrite, "email.send.receipt",
                DefaultMaxInputTokens: 4000,
                DefaultDeadlineMilliseconds: 30000,
                DefaultMaxModelCalls: 0,
                DefaultMaxToolCalls: 1,
                AllowedCapabilities: ["email.send"],
                PermissionScopes: ["email:send"],
                ApprovalRequired: true),
            ["calendar.find_slots"] = new(
                "calendar.find_slots", 1, "calendar.read", 400, 0.95m, true, SideEffectClass.ReadOnly, "calendar.slot-proposal",
                DefaultMaxInputTokens: 3000,
                DefaultDeadlineMilliseconds: 20000,
                DefaultMaxModelCalls: 0,
                DefaultMaxToolCalls: 1,
                AllowedCapabilities: ["calendar.read"],
                PermissionScopes: ["calendar:read"]),
            ["database.answer"] = new(
                "database.answer", 1, "database.read", 600, 0.95m, true, SideEffectClass.ReadOnly, "database.answer",
                DefaultMaxInputTokens: 3000,
                DefaultDeadlineMilliseconds: 20000,
                DefaultMaxModelCalls: 0,
                DefaultMaxToolCalls: 1,
                AllowedCapabilities: ["database.read"],
                PermissionScopes: ["database:read"]),
            ["document.summarize"] = new(
                "document.summarize", 1, "text.summarization", 1200, 0.85m, true, SideEffectClass.None, "document.summary",
                DefaultMaxInputTokens: 8000,
                DefaultDeadlineMilliseconds: 45000,
                DefaultMaxModelCalls: 1,
                AllowedCapabilities: ["text.summarization"]),
            ["project.decision.get"] = new(
                "project.decision.get", 1, "project.memory.read", 400, 1m, true, SideEffectClass.ReadOnly, "project.decision",
                DefaultMaxInputTokens: 1000,
                DefaultDeadlineMilliseconds: 5000,
                DefaultMaxModelCalls: 0,
                DefaultMaxToolCalls: 1,
                AllowedCapabilities: ["project.memory.read"],
                PermissionScopes: ["project:read"]),
            ["project.fact.get"] = new(
                "project.fact.get", 1, "project.memory.read", 400, 1m, true, SideEffectClass.ReadOnly, "project.fact",
                DefaultMaxInputTokens: 1000,
                DefaultDeadlineMilliseconds: 5000,
                DefaultMaxModelCalls: 0,
                DefaultMaxToolCalls: 1,
                AllowedCapabilities: ["project.memory.read"],
                PermissionScopes: ["project:read"])
        };

    public Task<TaskDefinition?> FindAsync(string taskType, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(Definitions.GetValueOrDefault(taskType));
    }
}

public sealed class BuiltInModelProfileRegistry : IModelProfileRegistry
{
    private static readonly IReadOnlyList<ModelProfile> Profiles =
    [
        new(
            "text.generation.small.eval-v1",
            "text.generation",
            ModelTier.Small,
            ["email.draft"],
            0.84m,
            0.004m,
            900,
            0.06m,
            0.98m,
            0.99m,
            8_000,
            1_500),
        new(
            "text.generation.strong.eval-v1",
            "text.generation",
            ModelTier.Strong,
            ["email.draft"],
            0.94m,
            0.020m,
            2_200,
            0.03m,
            0.99m,
            0.995m,
            32_000,
            4_000),
        new(
            "text.summarization.small.eval-v1",
            "text.summarization",
            ModelTier.Small,
            ["document.summarize"],
            0.87m,
            0.006m,
            1_100,
            0.06m,
            0.98m,
            0.99m,
            12_000,
            1_500),
        new(
            "text.summarization.strong.eval-v1",
            "text.summarization",
            ModelTier.Strong,
            ["document.summarize"],
            0.95m,
            0.025m,
            2_800,
            0.03m,
            0.99m,
            0.995m,
            64_000,
            4_000)
    ];

    public Task<IReadOnlyList<ModelProfile>> ListAsync(
        string capability,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult<IReadOnlyList<ModelProfile>>(Profiles
            .Where(item => string.Equals(item.Capability, capability, StringComparison.Ordinal))
            .OrderBy(item => item.EstimatedCost)
            .ThenBy(item => item.ExpectedLatencyMilliseconds)
            .ToArray());
    }
}

public sealed record ModelGatewayOptions
{
    public string Mode { get; init; } = "Deterministic";
    public string? BaseUrl { get; init; }
    public string? ApiKey { get; init; }
}

public sealed class DevelopmentExternalActionExecutor : IExternalActionExecutor
{
    public Task<ExternalActionResult> ExecuteAsync(
        ExternalActionRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!string.Equals(request.Capability, "email.send", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"The development external-action executor does not implement '{request.Capability}'.");
        }

        var output = JsonSerializer.SerializeToElement(new
        {
            receiptId = $"sim-{request.IdempotencyKey[..24]}",
            status = "simulated",
            capability = request.Capability
        });
        return Task.FromResult(new ExternalActionResult(
            output,
            [new EvidenceReference("external-action", $"receipt:{request.IdempotencyKey}")]));
    }
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
