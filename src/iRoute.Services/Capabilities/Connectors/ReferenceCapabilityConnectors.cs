using System.Collections.Frozen;
using System.Text.Json;
using iRoute.Common;

namespace iRoute.Services;

public sealed class ReferenceEmailConnector : ICapabilityConnector
{
    public string ConnectorId => "reference-email-v1";
    public string Transport => "email";

    public bool Supports(CapabilityDefinition definition) => definition.Capability is
        "email.read" or "email.draft.compose" or "email.send";

    public Task<CapabilityConnectorResult> InvokeAsync(
        CapabilityInvocationRequest request,
        CapabilityDefinition definition,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(definition.Capability switch
        {
            "email.read" => Read(request),
            "email.draft.compose" => Draft(request),
            "email.send" => Send(request),
            _ => throw Invalid(request, ConnectorId, "The email operation is not registered.")
        });
    }

    private static CapabilityConnectorResult Read(CapabilityInvocationRequest request)
    {
        var query = JsonInput.OptionalString(request.Input, "query") ?? "recent project updates";
        var messageId = "msg-reference-001";
        var output = JsonSerializer.SerializeToElement(new
        {
            query,
            messages = new[]
            {
                new
                {
                    messageId,
                    from = "ada@example.com",
                    subject = "Project status",
                    receivedAt = "2026-07-31T09:00:00Z",
                    snippet = "The milestone is ready for review."
                }
            },
            count = 1
        });
        return new CapabilityConnectorResult(
            output,
            1m,
            [new EvidenceReference("email-message", messageId)]);
    }

    private static CapabilityConnectorResult Draft(CapabilityInvocationRequest request)
    {
        var recipient = JsonInput.RequiredString(request, "recipient", ConnectorIdStatic);
        var subject = JsonInput.OptionalString(request.Input, "subject") ?? "Project update";
        var body = JsonInput.OptionalString(request.Input, "body")
            ?? JsonInput.OptionalString(request.Input, "objective")
            ?? "Here is the requested project update.";
        return new CapabilityConnectorResult(
            JsonSerializer.SerializeToElement(new { recipients = new[] { recipient }, subject, body }),
            0.98m,
            [new EvidenceReference("request", request.CorrelationId)]);
    }

    private static CapabilityConnectorResult Send(CapabilityInvocationRequest request)
    {
        _ = JsonInput.OptionalString(request.Input, "recipient")
            ?? JsonInput.OptionalString(request.Input, "to")
            ?? throw JsonInput.Invalid(
                request,
                ConnectorIdStatic,
                "Capability input requires a non-empty 'recipient' or 'to' string.");
        _ = JsonInput.RequiredString(request, "subject", ConnectorIdStatic);
        _ = JsonInput.RequiredString(request, "body", ConnectorIdStatic);
        var reference = request.IdempotencyReference!;
        var receiptId = $"sim-{reference[..Math.Min(24, reference.Length)]}";
        return new CapabilityConnectorResult(
            JsonSerializer.SerializeToElement(new
            {
                receiptId,
                status = "simulated",
                capability = request.Capability
            }),
            1m,
            [new EvidenceReference("external-action", $"receipt:{reference}")]);
    }

    private const string ConnectorIdStatic = "reference-email-v1";

    private static CapabilityInvocationException Invalid(
        CapabilityInvocationRequest request,
        string connectorId,
        string message) => JsonInput.Invalid(request, connectorId, message);
}

public sealed class ReferenceCalendarConnector : ICapabilityConnector
{
    public string ConnectorId => "reference-calendar-v1";
    public string Transport => "calendar";
    public bool Supports(CapabilityDefinition definition) => definition.Capability == "calendar.read";

    public Task<CapabilityConnectorResult> InvokeAsync(
        CapabilityInvocationRequest request,
        CapabilityDefinition definition,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var timezone = JsonInput.OptionalString(request.Input, "timezone") ?? "Europe/Tirane";
        var output = JsonSerializer.SerializeToElement(new
        {
            timezone,
            slots = new[]
            {
                new { start = "2026-08-03T09:00:00+02:00", end = "2026-08-03T09:30:00+02:00" },
                new { start = "2026-08-03T14:00:00+02:00", end = "2026-08-03T14:30:00+02:00" }
            }
        });
        return Task.FromResult(new CapabilityConnectorResult(
            output,
            1m,
            [new EvidenceReference("calendar", $"tenant:{request.TenantId}:availability")]));
    }
}

public sealed class ReferenceDatabaseConnector : ICapabilityConnector
{
    private static readonly FrozenSet<string> AllowedQueries = new[]
    {
        "project-status",
        "open-actions"
    }.ToFrozenSet(StringComparer.Ordinal);

    public string ConnectorId => "reference-database-v1";
    public string Transport => "database";
    public bool Supports(CapabilityDefinition definition) => definition.Capability == "database.read";

    public Task<CapabilityConnectorResult> InvokeAsync(
        CapabilityInvocationRequest request,
        CapabilityDefinition definition,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (request.Input.ValueKind == JsonValueKind.Object &&
            (request.Input.TryGetProperty("sql", out _) || request.Input.TryGetProperty("query", out _)))
        {
            throw JsonInput.Invalid(request, ConnectorId, "Raw database queries are disabled; use a registered queryId.");
        }

        var queryId = JsonInput.OptionalString(request.Input, "queryId") ?? "project-status";
        if (!AllowedQueries.Contains(queryId))
        {
            throw JsonInput.Invalid(request, ConnectorId, $"Database queryId '{queryId}' is not registered.");
        }

        var output = queryId == "open-actions"
            ? JsonSerializer.SerializeToElement(new
            {
                queryId,
                answer = "Two open actions require review.",
                rowCount = 2,
                rows = new[]
                {
                    new { action = "Review W11 connector contract", status = "open" },
                    new { action = "Prepare W12 lifecycle plan", status = "open" }
                }
            })
            : JsonSerializer.SerializeToElement(new
            {
                queryId,
                answer = "The iRoute implementation is on workstream W11.",
                rowCount = 1,
                rows = new[] { new { project = "iRoute", workstream = "W11", status = "in_progress" } }
            });
        return Task.FromResult(new CapabilityConnectorResult(
            output,
            1m,
            [new EvidenceReference("database-query", $"tenant:{request.TenantId}:query:{queryId}")]));
    }
}

public sealed class ReferenceOpenApiConnector : ICapabilityConnector
{
    public string ConnectorId => "reference-openapi-v1";
    public string Transport => "https";
    public bool Supports(CapabilityDefinition definition) => definition.Capability == "openapi.invoke";

    public Task<CapabilityConnectorResult> InvokeAsync(
        CapabilityInvocationRequest request,
        CapabilityDefinition definition,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (request.Input.ValueKind == JsonValueKind.Object &&
            (request.Input.TryGetProperty("url", out _) ||
             request.Input.TryGetProperty("host", out _) ||
             request.Input.TryGetProperty("method", out _)))
        {
            throw JsonInput.Invalid(request, ConnectorId, "Arbitrary OpenAPI destinations and methods are disabled.");
        }

        var operationId = JsonInput.RequiredString(request, "operationId", ConnectorId);
        if (!string.Equals(operationId, "getProjectStatus", StringComparison.Ordinal))
        {
            throw JsonInput.Invalid(request, ConnectorId, $"OpenAPI operationId '{operationId}' is not registered.");
        }

        var output = JsonSerializer.SerializeToElement(new
        {
            operationId,
            status = 200,
            data = new { project = request.ProjectId ?? "unscoped", state = "active" }
        });
        return Task.FromResult(new CapabilityConnectorResult(
            output,
            0.99m,
            [new EvidenceReference("openapi-operation", "project-service:getProjectStatus")]));
    }
}

public sealed class ReferenceMcpConnector : ICapabilityConnector
{
    public string ConnectorId => "reference-mcp-v1";
    public string Transport => "mcp";
    public bool Supports(CapabilityDefinition definition) => definition.Capability == "mcp.invoke";

    public Task<CapabilityConnectorResult> InvokeAsync(
        CapabilityInvocationRequest request,
        CapabilityDefinition definition,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var server = JsonInput.RequiredString(request, "server", ConnectorId);
        var tool = JsonInput.RequiredString(request, "tool", ConnectorId);
        if (!string.Equals(server, "project-tools", StringComparison.Ordinal) ||
            !string.Equals(tool, "get_project_summary", StringComparison.Ordinal))
        {
            throw JsonInput.Invalid(request, ConnectorId, $"MCP tool '{server}/{tool}' is not registered.");
        }

        // A real transport may also return instructions, logs, or hidden metadata. The connector
        // deliberately projects only this typed data member into the normalized envelope.
        var output = JsonSerializer.SerializeToElement(new
        {
            server,
            tool,
            data = new { project = request.ProjectId ?? "unscoped", summary = "The project is active." }
        });
        return Task.FromResult(new CapabilityConnectorResult(
            output,
            0.97m,
            [new EvidenceReference("mcp-tool", $"{server}:{tool}")]));
    }
}

public sealed class ReferenceAgentResultConnector(TimeProvider clock) : ICapabilityConnector
{
    public string ConnectorId => "reference-agent-result-v1";
    public string Transport => "agent-result";
    public bool Supports(CapabilityDefinition definition) => definition.Capability == "agent.result.ingest";

    public Task<CapabilityConnectorResult> InvokeAsync(
        CapabilityInvocationRequest request,
        CapabilityDefinition definition,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var schemaVersion = JsonInput.RequiredString(request, "schemaVersion", ConnectorId);
        var provenance = JsonInput.RequiredString(request, "provenance", ConnectorId);
        var observedAtText = JsonInput.RequiredString(request, "observedAt", ConnectorId);
        if (!string.Equals(schemaVersion, "1.0", StringComparison.Ordinal))
        {
            throw JsonInput.Invalid(request, ConnectorId, $"Agent result schemaVersion '{schemaVersion}' is unsupported.");
        }

        if (!DateTimeOffset.TryParse(observedAtText, out var observedAt) ||
            observedAt > clock.GetUtcNow().AddMinutes(1) ||
            clock.GetUtcNow() - observedAt > TimeSpan.FromMinutes(5))
        {
            throw JsonInput.Invalid(request, ConnectorId, "The agent result is stale or has an invalid observedAt value.");
        }

        if (!request.Input.TryGetProperty("dependencies", out var dependencies) ||
            dependencies.ValueKind != JsonValueKind.Array ||
            dependencies.GetArrayLength() == 0)
        {
            throw JsonInput.Invalid(request, ConnectorId, "The agent result must declare at least one dependency.");
        }

        if (!request.Input.TryGetProperty("result", out var result) || result.ValueKind != JsonValueKind.Object)
        {
            throw JsonInput.Invalid(request, ConnectorId, "The agent result must contain a typed result object.");
        }

        JsonElement projected;
        if (result.TryGetProperty("summary", out var summary) && summary.ValueKind == JsonValueKind.String)
        {
            projected = JsonSerializer.SerializeToElement(new
            {
                schemaVersion,
                result = new { summary = summary.GetString() }
            });
        }
        else if (result.TryGetProperty("value", out var value))
        {
            projected = JsonSerializer.SerializeToElement(new { schemaVersion, result = new { value } });
        }
        else
        {
            throw JsonInput.Invalid(request, ConnectorId, "The typed agent result requires 'summary' or 'value'.");
        }

        return Task.FromResult(new CapabilityConnectorResult(
            projected,
            0.9m,
            [new EvidenceReference("agent-result", provenance, ObservedAt: observedAt)]));
    }
}

public sealed class CapabilityExternalActionExecutor(ICapabilityExecutor capabilities) : IExternalActionExecutor
{
    public async Task<ExternalActionResult> ExecuteAsync(
        ExternalActionRequest request,
        CancellationToken cancellationToken)
    {
        var result = await capabilities.ExecuteAsync(
            new CapabilityInvocationRequest(
                request.Capability,
                1,
                request.Input,
                request.TenantId,
                request.ActorId,
                request.ProjectId,
                request.PermissionScopes ?? [],
                request.PolicyVersion,
                request.SideEffectClass,
                request.DeadlineMilliseconds,
                64 * 1024,
                request.ExecutionId.ToString(),
                request.IdempotencyKey),
            cancellationToken);
        return new ExternalActionResult(result.Output, result.Evidence);
    }
}

internal static class JsonInput
{
    public static string RequiredString(
        CapabilityInvocationRequest request,
        string propertyName,
        string connectorId) => OptionalString(request.Input, propertyName)
            ?? throw Invalid(request, connectorId, $"Capability input requires a non-empty '{propertyName}' string.");

    public static string? OptionalString(JsonElement input, string propertyName)
    {
        if (input.ValueKind != JsonValueKind.Object ||
            !input.TryGetProperty(propertyName, out var value) ||
            value.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        var text = value.GetString()?.Trim();
        return string.IsNullOrWhiteSpace(text) ? null : text;
    }

    public static CapabilityInvocationException Invalid(
        CapabilityInvocationRequest request,
        string connectorId,
        string message) => new(
            ErrorCodes.InvalidTaskRequest,
            message,
            CapabilityFailureKind.InvalidRequest,
            false,
            request.Capability,
            connectorId,
            request.CorrelationId);
}
