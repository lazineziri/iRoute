using System.Buffers;
using System.Diagnostics;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using iRoute.Contracts;
using iRoute.Core;
using Microsoft.Extensions.Diagnostics.HealthChecks;
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
    public string GatewayId { get; init; } = "external";
    public ModelGatewayTransport Transport { get; init; } = ModelGatewayTransport.Buffered;
    public string? BaseUrl { get; init; }
    public string? ApiKey { get; init; }
    public string ExecutePath { get; init; } = "v1/execute";
    public string StreamPath { get; init; } = "v1/stream";
    public string HealthPath { get; init; } = "health";
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
            DateTimeOffset.UtcNow,
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

public sealed class GenericHttpModelGateway(
    HttpClient httpClient,
    IOptions<ModelGatewayOptions> configuredOptions) : IModelGateway
{
    private const int MaximumStreamEvents = 10_000;
    private const int MaximumStreamLineLength = 65_536;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly Encoding StrictUtf8 = new UTF8Encoding(false, true);
    private readonly ModelGatewayOptions _options = configuredOptions.Value;

    public string GatewayId => _options.GatewayId;

    public async Task<ModelGatewayResult> ExecuteAsync(
        ModelGatewayRequest request,
        CancellationToken cancellationToken)
    {
        if (_options.Transport == ModelGatewayTransport.Buffered)
        {
            return await ExecuteBufferedAsync(request, cancellationToken);
        }

        ModelGatewayResult? completed = null;
        await foreach (var streamEvent in StreamAsync(request, cancellationToken))
        {
            if (streamEvent.Kind == ModelGatewayStreamEventKind.Completed)
            {
                completed = streamEvent.Result;
            }
        }

        return completed ?? throw InvalidResponse(
            request,
            "The configured model gateway stream ended without a completed result.");
    }

    public async IAsyncEnumerable<ModelGatewayStreamEvent> StreamAsync(
        ModelGatewayRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        EnsureConfigured(request);
        if (_options.Transport == ModelGatewayTransport.Buffered)
        {
            yield return new ModelGatewayStreamEvent(
                1,
                ModelGatewayStreamEventKind.Completed,
                Result: await ExecuteBufferedAsync(request, cancellationToken));
            yield break;
        }

        using var response = await SendAsync(
            request,
            _options.StreamPath,
            "application/x-ndjson",
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        EnsureSuccess(response, request);
        await using var responseStream = await response.Content.ReadAsStreamAsync(cancellationToken);
        var previousSequence = 0L;
        var eventCount = 0;
        var completed = false;
        await foreach (var line in ReadBoundedLinesAsync(
            responseStream,
            request,
            cancellationToken))
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            eventCount++;
            if (eventCount > MaximumStreamEvents)
            {
                throw InvalidResponse(request, "The configured model gateway stream exceeded its safety bounds.");
            }

            ModelGatewayStreamEvent streamEvent;
            try
            {
                streamEvent = JsonSerializer.Deserialize<ModelGatewayStreamEvent>(line, JsonOptions)
                    ?? throw InvalidResponse(request, "The configured model gateway stream returned an empty event.");
            }
            catch (JsonException exception)
            {
                throw InvalidResponse(
                    request,
                    "The configured model gateway stream returned invalid NDJSON.",
                    exception);
            }

            if (streamEvent.Sequence <= previousSequence)
            {
                throw InvalidResponse(request, "Model gateway stream event sequences must increase monotonically.");
            }

            previousSequence = streamEvent.Sequence;
            streamEvent = NormalizeStreamEvent(streamEvent, request);
            if (completed)
            {
                throw InvalidResponse(request, "The configured model gateway emitted data after completion.");
            }

            completed = streamEvent.Kind == ModelGatewayStreamEventKind.Completed;
            yield return streamEvent;
        }

        if (!completed)
        {
            throw InvalidResponse(request, "The configured model gateway stream ended without a completed event.");
        }
    }

    public async Task<ModelGatewayHealth> CheckHealthAsync(CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_options.BaseUrl) || httpClient.BaseAddress is null)
        {
            return new ModelGatewayHealth(
                _options.GatewayId,
                ModelGatewayHealthStatus.Unavailable,
                0,
                DateTimeOffset.UtcNow,
                "ModelGateway:BaseUrl is not configured with an absolute URI.");
        }

        var stopwatch = Stopwatch.StartNew();
        try
        {
            using var message = CreateMessage(HttpMethod.Get, _options.HealthPath, null);
            using var response = await httpClient.SendAsync(
                message,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);
            stopwatch.Stop();
            if (!response.IsSuccessStatusCode)
            {
                return new ModelGatewayHealth(
                    _options.GatewayId,
                    ModelGatewayHealthStatus.Unavailable,
                    stopwatch.ElapsedMilliseconds,
                    DateTimeOffset.UtcNow,
                    $"Gateway health probe returned HTTP {(int)response.StatusCode}.");
            }

            try
            {
                var reported = await response.Content.ReadFromJsonAsync<ModelGatewayHealth>(
                    JsonOptions,
                    cancellationToken);
                return reported is null || !Enum.IsDefined(reported.Status)
                    ? new ModelGatewayHealth(
                        _options.GatewayId,
                        ModelGatewayHealthStatus.Degraded,
                        stopwatch.ElapsedMilliseconds,
                        DateTimeOffset.UtcNow,
                        "Gateway health probe returned an empty response.")
                    : reported with
                    {
                        GatewayId = _options.GatewayId,
                        LatencyMilliseconds = stopwatch.ElapsedMilliseconds,
                        CheckedAt = DateTimeOffset.UtcNow
                    };
            }
            catch (JsonException)
            {
                return new ModelGatewayHealth(
                    _options.GatewayId,
                    ModelGatewayHealthStatus.Degraded,
                    stopwatch.ElapsedMilliseconds,
                    DateTimeOffset.UtcNow,
                    "Gateway health probe returned invalid JSON.");
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (HttpRequestException)
        {
            stopwatch.Stop();
            return new ModelGatewayHealth(
                _options.GatewayId,
                ModelGatewayHealthStatus.Unavailable,
                stopwatch.ElapsedMilliseconds,
                DateTimeOffset.UtcNow,
                "Gateway health probe could not reach the configured endpoint.");
        }
    }

    private async Task<ModelGatewayResult> ExecuteBufferedAsync(
        ModelGatewayRequest request,
        CancellationToken cancellationToken)
    {
        EnsureConfigured(request);
        using var response = await SendAsync(
            request,
            _options.ExecutePath,
            "application/json",
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        EnsureSuccess(response, request);
        try
        {
            var result = await response.Content.ReadFromJsonAsync<ModelGatewayResult>(
                JsonOptions,
                cancellationToken);
            return result is null
                ? throw InvalidResponse(request, "The configured model gateway returned an empty response.")
                : NormalizeResult(result, request, ModelGatewayTransport.Buffered);
        }
        catch (JsonException exception)
        {
            throw InvalidResponse(
                request,
                "The configured model gateway returned invalid JSON.",
                exception);
        }
    }

    private async Task<HttpResponseMessage> SendAsync(
        ModelGatewayRequest request,
        string path,
        string accept,
        HttpCompletionOption completionOption,
        CancellationToken cancellationToken)
    {
        using var message = CreateMessage(HttpMethod.Post, path, request);
        message.Headers.Accept.ParseAdd(accept);
        try
        {
            return await httpClient.SendAsync(message, completionOption, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (TaskCanceledException exception)
        {
            throw new ModelGatewayException(
                ErrorCodes.ModelGatewayUnavailable,
                "The configured model gateway timed out.",
                true,
                innerException: exception,
                failureKind: ModelGatewayFailureKind.Timeout,
                gatewayId: _options.GatewayId,
                correlationId: request.CorrelationId);
        }
        catch (HttpRequestException exception)
        {
            throw new ModelGatewayException(
                ErrorCodes.ModelGatewayUnavailable,
                "The configured model gateway could not be reached.",
                true,
                innerException: exception,
                failureKind: ModelGatewayFailureKind.Unavailable,
                gatewayId: _options.GatewayId,
                correlationId: request.CorrelationId);
        }
    }

    private HttpRequestMessage CreateMessage(
        HttpMethod method,
        string path,
        ModelGatewayRequest? request)
    {
        var message = new HttpRequestMessage(method, path);
        if (request is not null)
        {
            message.Content = JsonContent.Create(request, options: JsonOptions);
        }

        if (!string.IsNullOrWhiteSpace(_options.ApiKey))
        {
            message.Headers.Authorization = new("Bearer", _options.ApiKey);
        }

        if (!string.IsNullOrWhiteSpace(request?.CorrelationId))
        {
            message.Headers.TryAddWithoutValidation("X-Correlation-Id", request.CorrelationId);
        }

        if (request?.DeadlineMilliseconds is { } deadline)
        {
            message.Headers.TryAddWithoutValidation("X-Deadline-Milliseconds", deadline.ToString(
                System.Globalization.CultureInfo.InvariantCulture));
        }

        return message;
    }

    private void EnsureSuccess(HttpResponseMessage response, ModelGatewayRequest request)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        var statusCode = (int)response.StatusCode;
        var kind = statusCode switch
        {
            400 or 404 or 409 or 422 => ModelGatewayFailureKind.InvalidRequest,
            401 or 403 => ModelGatewayFailureKind.Authentication,
            408 => ModelGatewayFailureKind.Timeout,
            429 => ModelGatewayFailureKind.RateLimited,
            >= 500 => ModelGatewayFailureKind.Unavailable,
            _ => ModelGatewayFailureKind.Internal
        };
        var retryable = kind is ModelGatewayFailureKind.Timeout or
            ModelGatewayFailureKind.RateLimited or
            ModelGatewayFailureKind.Unavailable;
        throw new ModelGatewayException(
            ErrorCodes.ModelGatewayHttpError,
            $"The configured model gateway returned HTTP {statusCode}.",
            retryable,
            statusCode,
            failureKind: kind,
            gatewayId: _options.GatewayId,
            correlationId: request.CorrelationId);
    }

    private ModelGatewayStreamEvent NormalizeStreamEvent(
        ModelGatewayStreamEvent streamEvent,
        ModelGatewayRequest request) =>
        streamEvent.Kind switch
        {
            ModelGatewayStreamEventKind.OutputDelta when !string.IsNullOrEmpty(streamEvent.Delta) => streamEvent,
            ModelGatewayStreamEventKind.Usage when streamEvent.Usage is { } usage =>
                streamEvent with { Usage = NormalizeUsage(usage, request) },
            ModelGatewayStreamEventKind.Completed when streamEvent.Result is { } result =>
                streamEvent with
                {
                    Result = NormalizeResult(result, request, ModelGatewayTransport.Streaming)
                },
            _ => throw InvalidResponse(
                request,
                $"The configured model gateway returned an invalid {streamEvent.Kind} stream event.")
        };

    private ModelGatewayResult NormalizeResult(
        ModelGatewayResult result,
        ModelGatewayRequest request,
        ModelGatewayTransport transport)
    {
        if (result.Output.ValueKind == JsonValueKind.Undefined ||
            result.Confidence is < 0m or > 1m ||
            result.Evidence is null ||
            !Enum.IsDefined(result.FinishReason))
        {
            throw InvalidResponse(request, "The configured model gateway returned an invalid result envelope.");
        }

        return result with
        {
            Usage = NormalizeUsage(result.Usage, request) with
            {
                ModelCalls = Math.Max(1, result.Usage.ModelCalls)
            },
            GatewayId = _options.GatewayId,
            Transport = transport
        };
    }

    private UsageSummary NormalizeUsage(
        UsageSummary usage,
        ModelGatewayRequest request)
    {
        if (usage.InputTokens < 0 ||
            usage.OutputTokens < 0 ||
            usage.Cost < 0m ||
            usage.DurationMilliseconds < 0 ||
            usage.ModelCalls < 0 ||
            usage.ToolCalls < 0)
        {
            throw new ModelGatewayException(
                ErrorCodes.ModelGatewayInvalidResponse,
                "The configured model gateway returned invalid normalized usage.",
                false,
                failureKind: ModelGatewayFailureKind.InvalidResponse,
                gatewayId: _options.GatewayId,
                correlationId: request.CorrelationId);
        }

        return usage;
    }

    private void EnsureConfigured(ModelGatewayRequest? request)
    {
        if (string.IsNullOrWhiteSpace(_options.BaseUrl) || httpClient.BaseAddress is null)
        {
            throw new ModelGatewayException(
                ErrorCodes.ModelGatewayUnavailable,
                "ModelGateway:BaseUrl is not configured with an absolute URI.",
                false,
                failureKind: ModelGatewayFailureKind.InvalidRequest,
                gatewayId: _options.GatewayId,
                correlationId: request?.CorrelationId);
        }
    }

    private ModelGatewayException InvalidResponse(
        ModelGatewayRequest request,
        string message,
        Exception? innerException = null) =>
        new(
            ErrorCodes.ModelGatewayInvalidResponse,
            message,
            false,
            innerException: innerException,
            failureKind: ModelGatewayFailureKind.InvalidResponse,
            gatewayId: _options.GatewayId,
            correlationId: request.CorrelationId);

    private async IAsyncEnumerable<string> ReadBoundedLinesAsync(
        Stream stream,
        ModelGatewayRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var readBuffer = ArrayPool<byte>.Shared.Rent(4_096);
        var lineBuffer = new ArrayBufferWriter<byte>();
        try
        {
            while (true)
            {
                var read = await stream.ReadAsync(readBuffer.AsMemory(), cancellationToken);
                if (read == 0)
                {
                    break;
                }

                for (var index = 0; index < read; index++)
                {
                    var value = readBuffer[index];
                    if (value == (byte)'\n')
                    {
                        var line = DecodeLine(lineBuffer.WrittenSpan, request);
                        lineBuffer.Clear();
                        yield return line.EndsWith('\r') ? line[..^1] : line;
                        continue;
                    }

                    if (lineBuffer.WrittenCount >= MaximumStreamLineLength)
                    {
                        throw InvalidResponse(
                            request,
                            "The configured model gateway stream exceeded its line-size bound.");
                    }

                    lineBuffer.GetSpan(1)[0] = value;
                    lineBuffer.Advance(1);
                }
            }

            if (lineBuffer.WrittenCount > 0)
            {
                yield return DecodeLine(lineBuffer.WrittenSpan, request);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(readBuffer);
        }
    }

    private string DecodeLine(
        ReadOnlySpan<byte> value,
        ModelGatewayRequest request)
    {
        try
        {
            return StrictUtf8.GetString(value);
        }
        catch (DecoderFallbackException exception)
        {
            throw InvalidResponse(
                request,
                "The configured model gateway stream returned invalid UTF-8.",
                exception);
        }
    }
}

public sealed class ModelGatewayHealthCheck(IModelGateway gateway) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        var health = await gateway.CheckHealthAsync(cancellationToken);
        var data = new Dictionary<string, object>(StringComparer.Ordinal)
        {
            ["gatewayId"] = health.GatewayId,
            ["latencyMilliseconds"] = health.LatencyMilliseconds,
            ["checkedAt"] = health.CheckedAt
        };
        return health.Status switch
        {
            ModelGatewayHealthStatus.Healthy => HealthCheckResult.Healthy(health.Message, data),
            ModelGatewayHealthStatus.Degraded => HealthCheckResult.Degraded(health.Message, data: data),
            _ => HealthCheckResult.Unhealthy(health.Message, data: data)
        };
    }
}
