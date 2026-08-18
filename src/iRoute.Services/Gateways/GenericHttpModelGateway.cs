using System.Diagnostics;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.Json;
using iRoute.Common;
using Microsoft.Extensions.Options;

namespace iRoute.Services;

public sealed class GenericHttpModelGateway(
    HttpClient httpClient,
    IOptions<ModelGatewayOptions> configuredOptions,
    TimeProvider clock) : IModelGateway
{
    private const int MaximumStreamEvents = 10_000;
    private static readonly JsonSerializerOptions JsonOptions = CreateJsonOptions();
    private readonly ModelGatewayOptions _options = configuredOptions.Value;

    private static JsonSerializerOptions CreateJsonOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        options.TypeInfoResolverChain.Add(ModelGatewayJsonContext.Default);
        return options;
    }

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
        await foreach (var line in BoundedNdjsonReader.ReadLinesAsync(
            responseStream,
            (message, exception) => InvalidResponse(request, message, exception),
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
                clock.GetUtcNow(),
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
                    clock.GetUtcNow(),
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
                        clock.GetUtcNow(),
                        "Gateway health probe returned an empty response.")
                    : reported with
                    {
                        GatewayId = _options.GatewayId,
                        LatencyMilliseconds = stopwatch.ElapsedMilliseconds,
                        CheckedAt = clock.GetUtcNow()
                    };
            }
            catch (JsonException)
            {
                return new ModelGatewayHealth(
                    _options.GatewayId,
                    ModelGatewayHealthStatus.Degraded,
                    stopwatch.ElapsedMilliseconds,
                    clock.GetUtcNow(),
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
                clock.GetUtcNow(),
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
                correlationId: request.CorrelationId,
                failureClass: GatewayFailureClass.Timeout);
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
                correlationId: request.CorrelationId,
                failureClass: GatewayFailureClass.Transport);
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
        var failureClass = kind switch
        {
            ModelGatewayFailureKind.Timeout => GatewayFailureClass.Timeout,
            ModelGatewayFailureKind.RateLimited => GatewayFailureClass.Throttling,
            ModelGatewayFailureKind.Unavailable => GatewayFailureClass.Provider,
            _ => GatewayFailureClass.Permanent
        };
        throw new ModelGatewayException(
            ErrorCodes.ModelGatewayHttpError,
            $"The configured model gateway returned HTTP {statusCode}.",
            retryable,
            statusCode,
            failureKind: kind,
            gatewayId: _options.GatewayId,
            correlationId: request.CorrelationId,
            retryAfter: RetryAfter(response),
            failureClass: failureClass);
    }

    private TimeSpan? RetryAfter(HttpResponseMessage response)
    {
        if (response.Headers.RetryAfter?.Delta is { } delta)
        {
            return delta < TimeSpan.Zero ? TimeSpan.Zero : delta;
        }

        if (response.Headers.RetryAfter?.Date is { } date)
        {
            var remaining = date - clock.GetUtcNow();
            return remaining < TimeSpan.Zero ? TimeSpan.Zero : remaining;
        }

        return null;
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
                correlationId: request.CorrelationId,
                failureClass: GatewayFailureClass.MalformedOutput);
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
                correlationId: request?.CorrelationId,
                failureClass: GatewayFailureClass.Permanent);
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
            correlationId: request.CorrelationId,
            failureClass: GatewayFailureClass.MalformedOutput);

}
