using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using iRoute.Contracts;

namespace iRoute.Sdk.DotNet;

public sealed record IRouteClientOptions(
    string? TenantId = null,
    string? ActorId = null,
    IReadOnlyCollection<string>? PermissionScopes = null,
    string? BearerToken = null);

public sealed record ObservabilityQueryOptions(
    DateTimeOffset? From = null,
    DateTimeOffset? To = null,
    string? TaskType = null,
    string? PolicyVersion = null);

public sealed class IRouteApiException(
    HttpStatusCode statusCode,
    string? code,
    string? title,
    string? detail,
    string responseBody) : HttpRequestException(
        detail ?? title ?? $"iRoute request failed with HTTP {(int)statusCode}.",
        null,
        statusCode)
{
    public string? Code { get; } = code;
    public string? Title { get; } = title;
    public string? Detail { get; } = detail;
    public string ResponseBody { get; } = responseBody;
}

public sealed class IRouteClient(HttpClient httpClient, IRouteClientOptions? options = null)
{
    private static readonly JsonSerializerOptions JsonOptions = CreateJsonOptions();
    private readonly IRouteClientOptions _options = options ?? new();

    private static JsonSerializerOptions CreateJsonOptions()
    {
        var serializerOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };
        serializerOptions.TypeInfoResolverChain.Add(IRouteSdkJsonContext.Default);
        return serializerOptions;
    }

    public async Task<ExecutionSnapshot> ExecuteAsync(
        TaskRequest request,
        CancellationToken cancellationToken = default)
    {
        using var message = new HttpRequestMessage(HttpMethod.Post, "v1/executions")
        {
            Content = JsonContent.Create(request, options: JsonOptions)
        };
        AddScopeHeaders(message, request.TenantId, request.ActorId, request.PermissionScopes);
        if (!string.IsNullOrWhiteSpace(request.IdempotencyKey))
        {
            message.Headers.TryAddWithoutValidation("Idempotency-Key", request.IdempotencyKey);
        }

        using var response = await httpClient.SendAsync(message, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        return await response.Content.ReadFromJsonAsync<ExecutionSnapshot>(JsonOptions, cancellationToken)
            ?? throw new InvalidOperationException("iRoute returned an empty response.");
    }

    public async Task<ExecutionSnapshot?> GetAsync(
        Guid executionId,
        CancellationToken cancellationToken = default)
    {
        using var message = CreateScopedRequest(HttpMethod.Get, $"v1/executions/{executionId}");
        using var response = await httpClient.SendAsync(message, cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        await EnsureSuccessAsync(response, cancellationToken);
        return await response.Content.ReadFromJsonAsync<ExecutionSnapshot>(JsonOptions, cancellationToken);
    }

    public async Task<bool> CancelAsync(Guid executionId, CancellationToken cancellationToken = default)
    {
        using var message = CreateScopedRequest(HttpMethod.Post, $"v1/executions/{executionId}/cancel");
        using var response = await httpClient.SendAsync(message, cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return false;
        }

        await EnsureSuccessAsync(response, cancellationToken);
        return true;
    }

    public async Task<ApprovalResult> SubmitApprovalAsync(
        Guid executionId,
        ApprovalDecision decision,
        CancellationToken cancellationToken = default)
    {
        using var message = CreateScopedRequest(HttpMethod.Post, $"v1/executions/{executionId}/approvals");
        message.Content = JsonContent.Create(decision, options: JsonOptions);
        using var response = await httpClient.SendAsync(message, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        return await response.Content.ReadFromJsonAsync<ApprovalResult>(JsonOptions, cancellationToken)
            ?? throw new InvalidOperationException("iRoute returned an empty approval response.");
    }

    public async Task<ArtifactSnapshot?> GetArtifactAsync(
        Guid artifactId,
        CancellationToken cancellationToken = default)
    {
        using var message = CreateScopedRequest(HttpMethod.Get, $"v1/artifacts/{artifactId}");
        using var response = await httpClient.SendAsync(message, cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        await EnsureSuccessAsync(response, cancellationToken);
        return await response.Content.ReadFromJsonAsync<ArtifactSnapshot>(JsonOptions, cancellationToken);
    }

    public async Task<ModelGatewayHealth> GetModelGatewayHealthAsync(
        CancellationToken cancellationToken = default)
    {
        using var message = CreateScopedRequest(HttpMethod.Get, "health/model-gateway");
        using var response = await httpClient.SendAsync(message, cancellationToken);
        if (response.StatusCode is not HttpStatusCode.OK and not HttpStatusCode.ServiceUnavailable)
        {
            await EnsureSuccessAsync(response, cancellationToken);
        }

        return await response.Content.ReadFromJsonAsync<ModelGatewayHealth>(JsonOptions, cancellationToken)
            ?? throw new InvalidOperationException("iRoute returned an empty model-gateway health response.");
    }

    public async Task<ObservabilitySummary> GetObservabilitySummaryAsync(
        ObservabilityQueryOptions? query = null,
        CancellationToken cancellationToken = default)
    {
        query ??= new ObservabilityQueryOptions();
        var parameters = new List<string>();
        AddQuery(parameters, "from", query.From?.ToString("O", CultureInfo.InvariantCulture));
        AddQuery(parameters, "to", query.To?.ToString("O", CultureInfo.InvariantCulture));
        AddQuery(parameters, "taskType", query.TaskType);
        AddQuery(parameters, "policyVersion", query.PolicyVersion);
        var path = "v1/observability/summary" +
            (parameters.Count == 0 ? string.Empty : $"?{string.Join('&', parameters)}");
        using var message = CreateScopedRequest(HttpMethod.Get, path);
        using var response = await httpClient.SendAsync(message, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        return await response.Content.ReadFromJsonAsync<ObservabilitySummary>(JsonOptions, cancellationToken)
            ?? throw new InvalidOperationException("iRoute returned an empty observability summary.");
    }

    public async Task<ExecutionTimeline?> GetExecutionTimelineAsync(
        Guid executionId,
        CancellationToken cancellationToken = default)
    {
        using var message = CreateScopedRequest(
            HttpMethod.Get,
            $"v1/observability/executions/{executionId}");
        using var response = await httpClient.SendAsync(message, cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        await EnsureSuccessAsync(response, cancellationToken);
        return await response.Content.ReadFromJsonAsync<ExecutionTimeline>(JsonOptions, cancellationToken);
    }

    public async IAsyncEnumerable<ExecutionEvent> StreamEventsAsync(
        Guid executionId,
        long afterSequence = 0,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var path = $"v1/executions/{executionId}/events?after={afterSequence.ToString(CultureInfo.InvariantCulture)}";
        using var message = CreateScopedRequest(HttpMethod.Get, path);
        message.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        using var response = await httpClient.SendAsync(
            message,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var reader = new StreamReader(stream);

        var data = new StringBuilder();
        while (await reader.ReadLineAsync(cancellationToken) is { } line)
        {
            if (line.Length == 0)
            {
                if (data.Length > 0)
                {
                    yield return DeserializeEvent(data.ToString());
                    data.Clear();
                }

                continue;
            }

            if (line.StartsWith("data:", StringComparison.Ordinal))
            {
                var value = line[5..];
                if (data.Length > 0)
                {
                    data.Append('\n');
                }

                data.Append(value.Length > 0 && value[0] == ' ' ? value[1..] : value);
            }
        }

        // Some servers close immediately after a complete final data line. Preserve that event,
        // but treat an invalid partial frame as transport truncation rather than JSON failure.
        if (data.Length > 0 && TryDeserializeEvent(data.ToString(), out var finalEvent))
        {
            yield return finalEvent!;
        }
    }

    private static ExecutionEvent DeserializeEvent(string data) =>
        JsonSerializer.Deserialize<ExecutionEvent>(data, JsonOptions)
            ?? throw new InvalidOperationException("iRoute returned an invalid execution event.");

    private static bool TryDeserializeEvent(string data, out ExecutionEvent? executionEvent)
    {
        try
        {
            executionEvent = JsonSerializer.Deserialize<ExecutionEvent>(data, JsonOptions);
            return executionEvent is not null;
        }
        catch (JsonException)
        {
            executionEvent = null;
            return false;
        }
    }

    private HttpRequestMessage CreateScopedRequest(HttpMethod method, string path)
    {
        var message = new HttpRequestMessage(method, path);
        AddScopeHeaders(message, null, null, null);
        return message;
    }

    private void AddScopeHeaders(
        HttpRequestMessage message,
        string? tenantId,
        string? actorId,
        IReadOnlyCollection<string>? permissionScopes)
    {
        if (!string.IsNullOrWhiteSpace(_options.BearerToken))
        {
            message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _options.BearerToken);
        }

        AddOptionalHeader(message, "X-Tenant-Id", tenantId ?? _options.TenantId);
        AddOptionalHeader(message, "X-Actor-Id", actorId ?? _options.ActorId);
        var scopes = permissionScopes ?? _options.PermissionScopes;
        if (scopes is { Count: > 0 })
        {
            AddOptionalHeader(message, "X-Permission-Scopes", string.Join(' ', scopes));
        }
    }

    private static void AddOptionalHeader(HttpRequestMessage message, string name, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            message.Headers.TryAddWithoutValidation(name, value);
        }
    }

    private static void AddQuery(List<string> parameters, string name, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            parameters.Add($"{name}={Uri.EscapeDataString(value)}");
        }
    }

    private static async Task EnsureSuccessAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        string? code = null;
        string? title = null;
        string? detail = null;
        try
        {
            using var document = JsonDocument.Parse(body);
            if (document.RootElement.ValueKind == JsonValueKind.Object)
            {
                code = ReadString(document.RootElement, "code");
                title = ReadString(document.RootElement, "title");
                detail = ReadString(document.RootElement, "detail");
            }
        }
        catch (JsonException)
        {
        }

        throw new IRouteApiException(response.StatusCode, code, title, detail, body);
    }

    private static string? ReadString(JsonElement root, string propertyName) =>
        root.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}
