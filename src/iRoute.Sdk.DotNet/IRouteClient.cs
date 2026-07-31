using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.Json;
using iRoute.Contracts;

namespace iRoute.Sdk.DotNet;

public sealed record IRouteClientOptions(
    string? TenantId = null,
    string? ActorId = null,
    IReadOnlyCollection<string>? PermissionScopes = null);

public sealed class IRouteClient(HttpClient httpClient, IRouteClientOptions? options = null)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly IRouteClientOptions _options = options ?? new();

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
        response.EnsureSuccessStatusCode();
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

        response.EnsureSuccessStatusCode();
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

        response.EnsureSuccessStatusCode();
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
        response.EnsureSuccessStatusCode();
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

        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<ArtifactSnapshot>(JsonOptions, cancellationToken);
    }

    public async Task<ModelGatewayHealth> GetModelGatewayHealthAsync(
        CancellationToken cancellationToken = default)
    {
        using var response = await httpClient.GetAsync("health/model-gateway", cancellationToken);
        if (response.StatusCode is not HttpStatusCode.OK and not HttpStatusCode.ServiceUnavailable)
        {
            response.EnsureSuccessStatusCode();
        }

        return await response.Content.ReadFromJsonAsync<ModelGatewayHealth>(JsonOptions, cancellationToken)
            ?? throw new InvalidOperationException("iRoute returned an empty model-gateway health response.");
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
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var reader = new StreamReader(stream);

        string? data = null;
        while (await reader.ReadLineAsync(cancellationToken) is { } line)
        {
            if (line.Length == 0)
            {
                if (data is not null)
                {
                    yield return JsonSerializer.Deserialize<ExecutionEvent>(data, JsonOptions)
                        ?? throw new InvalidOperationException("iRoute returned an invalid execution event.");
                    data = null;
                }

                continue;
            }

            if (line.StartsWith("data:", StringComparison.Ordinal))
            {
                var value = line[5..];
                data = value.Length > 0 && value[0] == ' ' ? value[1..] : value;
            }
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
}
