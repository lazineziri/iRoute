using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using iRoute.Contracts;
using iRoute.Core;
using Microsoft.EntityFrameworkCore;

namespace iRoute.Infrastructure;

public sealed class InMemoryObservabilityStore(
    InMemoryExecutionStore executions,
    ObservabilityOptions options) : IObservabilityStore
{
    public Task<ObservabilitySummary> QueryAsync(
        ObservabilityQuery query,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ObservabilityProjection.Validate(query, options);
        var candidates = executions.ObservabilitySnapshot()
            .Where(item => ObservabilityProjection.Matches(item, query))
            .OrderByDescending(item => item.CreatedAt)
            .ThenByDescending(item => item.ExecutionId)
            .Take(options.MaxExecutions + 1)
            .ToArray();
        return Task.FromResult(ObservabilityProjection.Summarize(
            query,
            candidates.Take(options.MaxExecutions),
            candidates.Length > options.MaxExecutions,
            options.MaxRecentExecutions,
            executions.ObservabilityEvents));
    }

    public Task<ExecutionTimeline?> GetTimelineAsync(
        string tenantId,
        Guid executionId,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var snapshot = executions.ObservabilitySnapshot().SingleOrDefault(item =>
            item.ExecutionId == executionId &&
            string.Equals(item.TenantId, tenantId, StringComparison.Ordinal));
        return Task.FromResult(snapshot is null
            ? null
            : ObservabilityProjection.Timeline(
                snapshot,
                executions.ObservabilityEvents(executionId),
                options));
    }
}

public sealed class EfObservabilityStore(
    IDbContextFactory<IRouteDbContext> contextFactory,
    ObservabilityOptions options) : IObservabilityStore
{
    public async Task<ObservabilitySummary> QueryAsync(
        ObservabilityQuery query,
        CancellationToken cancellationToken)
    {
        ObservabilityProjection.Validate(query, options);
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var from = query.From.ToUnixTimeMilliseconds();
        var to = query.To.ToUnixTimeMilliseconds();
        var executionQuery = context.Executions.AsNoTracking().Where(item =>
            item.TenantId == query.TenantId &&
            item.CreatedAtUnixMilliseconds >= from &&
            item.CreatedAtUnixMilliseconds <= to);
        if (!string.IsNullOrWhiteSpace(query.TaskType))
        {
            executionQuery = executionQuery.Where(item => item.TaskType == query.TaskType);
        }

        var entities = await executionQuery
            .OrderByDescending(item => item.CreatedAtUnixMilliseconds)
            .ThenByDescending(item => item.ExecutionId)
            .Take(options.MaxExecutions + 1)
            .ToArrayAsync(cancellationToken);
        var snapshots = entities
            .Take(options.MaxExecutions)
            .Select(PersistenceMapping.ToContract)
            .ToArray();
        var ids = snapshots.Select(item => item.ExecutionId).ToArray();
        ExecutionEventEntity[] events = ids.Length == 0
            ? []
            : await context.ExecutionEvents
                .AsNoTracking()
                .Where(item => ids.Contains(item.ExecutionId))
                .OrderBy(item => item.ExecutionId)
                .ThenBy(item => item.Sequence)
                .ToArrayAsync(cancellationToken);
        var eventsByExecution = events
            .Select(PersistenceMapping.ToContract)
            .GroupBy(item => item.ExecutionId)
            .ToDictionary(group => group.Key, group => (IReadOnlyList<ExecutionEvent>)group.ToArray());
        return ObservabilityProjection.Summarize(
            query,
            snapshots,
            entities.Length > options.MaxExecutions,
            options.MaxRecentExecutions,
            id => eventsByExecution.GetValueOrDefault(id) ?? []);
    }

    public async Task<ExecutionTimeline?> GetTimelineAsync(
        string tenantId,
        Guid executionId,
        CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var entity = await context.Executions.AsNoTracking().SingleOrDefaultAsync(item =>
            item.TenantId == tenantId && item.ExecutionId == executionId,
            cancellationToken);
        if (entity is null)
        {
            return null;
        }

        var events = await context.ExecutionEvents
            .AsNoTracking()
            .Where(item => item.ExecutionId == executionId)
            .OrderBy(item => item.Sequence)
            .Take(options.MaxTimelineEvents + 1)
            .ToArrayAsync(cancellationToken);
        return ObservabilityProjection.Timeline(
            PersistenceMapping.ToContract(entity),
            events.Select(PersistenceMapping.ToContract).ToArray(),
            options);
    }
}

internal static class ObservabilityProjection
{
    private static readonly HashSet<string> MemoryResolvers = new(
        ["exact-cache", "fact-decision", "artifact-lookup", "semantic-memory"],
        StringComparer.Ordinal);
    private static readonly HashSet<string> RedactedProperties = new(
        [
            "input", "output", "content", "value", "body", "prompt", "response",
            "request", "raw", "credentials", "credential", "secret", "apiKey",
            "authorization", "headers", "password", "tenantId", "actorId", "projectId",
            "permissionScopes"
        ],
        StringComparer.OrdinalIgnoreCase);

    public static void Validate(ObservabilityQuery query, ObservabilityOptions options)
    {
        options.EnsureValid();
        if (string.IsNullOrWhiteSpace(query.TenantId))
        {
            throw new ArgumentException("TenantId is required.", nameof(query));
        }

        if (query.From > query.To)
        {
            throw new ArgumentException("The observability start time must not follow the end time.", nameof(query));
        }

        if (query.To - query.From > TimeSpan.FromDays(options.MaxQueryDays))
        {
            throw new ArgumentException(
                $"The observability window cannot exceed {options.MaxQueryDays} days.",
                nameof(query));
        }
    }

    public static bool Matches(ExecutionSnapshot snapshot, ObservabilityQuery query) =>
        string.Equals(snapshot.TenantId, query.TenantId, StringComparison.Ordinal) &&
        snapshot.CreatedAt >= query.From &&
        snapshot.CreatedAt <= query.To &&
        (string.IsNullOrWhiteSpace(query.TaskType) ||
         string.Equals(snapshot.TaskType, query.TaskType, StringComparison.Ordinal));

    public static ObservabilitySummary Summarize(
        ObservabilityQuery query,
        IEnumerable<ExecutionSnapshot> snapshots,
        bool truncated,
        int maxRecentExecutions,
        Func<Guid, IReadOnlyList<ExecutionEvent>> eventsFor)
    {
        var observations = snapshots.Select(snapshot =>
        {
            var events = eventsFor(snapshot.ExecutionId);
            return new ExecutionObservation(snapshot, events, PolicyVersion(snapshot, events));
        }).Where(item =>
            string.IsNullOrWhiteSpace(query.PolicyVersion) ||
            string.Equals(item.PolicyVersion, query.PolicyVersion, StringComparison.Ordinal))
          .ToArray();
        var groups = observations
            .GroupBy(item => (item.Snapshot.TaskType, item.PolicyVersion))
            .OrderBy(item => item.Key.TaskType, StringComparer.Ordinal)
            .ThenBy(item => item.Key.PolicyVersion, StringComparer.Ordinal)
            .Select(group => new ObservabilityMetricGroup(
                group.Key.TaskType,
                group.Key.PolicyVersion,
                Metrics(group)))
            .ToArray();
        var gatewayGroups = GatewayGroups(observations.SelectMany(item => item.Events));
        return new ObservabilitySummary(
            query.From,
            query.To,
            truncated,
            observations.Length,
            Metrics(observations),
            groups,
            MemoryDiagnostics(observations.SelectMany(item => item.Events)),
            observations
                .OrderByDescending(item => item.Snapshot.CreatedAt)
                .ThenByDescending(item => item.Snapshot.ExecutionId)
                .Take(maxRecentExecutions)
                .Select(RecentExecution)
                .ToArray(),
            gatewayGroups);
    }

    public static ExecutionTimeline Timeline(
        ExecutionSnapshot snapshot,
        IReadOnlyList<ExecutionEvent> sourceEvents,
        ObservabilityOptions options)
    {
        options.EnsureValid();
        var ordered = sourceEvents.OrderBy(item => item.Sequence).ToArray();
        var events = ordered.Take(options.MaxTimelineEvents).Select(item =>
            new ExecutionTimelineEntry(
                item.Sequence,
                item.Type,
                item.OccurredAt,
                Math.Max(0, (long)(item.OccurredAt - snapshot.CreatedAt).TotalMilliseconds),
                Redact(item.Data, options))).ToArray();
        return new ExecutionTimeline(
            snapshot.ExecutionId,
            TraceId(snapshot.ExecutionId, ordered),
            snapshot.TaskType,
            snapshot.TaskDefinitionVersion,
            PolicyVersion(snapshot, ordered),
            snapshot.Status,
            snapshot.CreatedAt,
            snapshot.UpdatedAt,
            Math.Max(0, (long)(snapshot.UpdatedAt - snapshot.CreatedAt).TotalMilliseconds),
            HashReference(snapshot.ActorId),
            string.IsNullOrWhiteSpace(snapshot.ProjectId) ? null : HashReference(snapshot.ProjectId),
            options.PayloadMode,
            ordered.Length > options.MaxTimelineEvents,
            events);
    }

    private static ObservabilityMetricSet Metrics(IEnumerable<ExecutionObservation> source)
    {
        var observations = source.ToArray();
        var completed = observations.Where(item =>
            item.Snapshot.Status == ExecutionStatus.Succeeded && item.Snapshot.Outcome is not null).ToArray();
        var failed = observations.Count(item => item.Snapshot.Status is
            ExecutionStatus.Failed or ExecutionStatus.Cancelled or ExecutionStatus.TimedOut);
        var inputTokens = completed.Sum(item => (long)item.Snapshot.Outcome!.Usage.InputTokens);
        var outputTokens = completed.Sum(item => (long)item.Snapshot.Outcome!.Usage.OutputTokens);
        var cost = completed.Sum(item => item.Snapshot.Outcome!.Usage.Cost);
        var modelCalls = completed.Sum(item => item.Snapshot.Outcome!.Usage.ModelCalls);
        var toolCalls = completed.Sum(item => item.Snapshot.Outcome!.Usage.ToolCalls);
        var noModel = completed.Count(item => IsNoModel(item.Snapshot.Outcome!.ResolutionLevel));
        return new ObservabilityMetricSet(
            observations.Length,
            completed.Length,
            failed,
            Ratio(completed.Length, observations.Length),
            completed.Length == 0 ? 0 : completed.Average(item => Quality(item.Snapshot.Outcome!)),
            inputTokens,
            outputTokens,
            cost,
            completed.Length == 0
                ? 0
                : (decimal)completed.Average(item =>
                    Math.Max(0, (item.Snapshot.UpdatedAt - item.Snapshot.CreatedAt).TotalMilliseconds)),
            modelCalls,
            toolCalls,
            noModel,
            noModel);
    }

    private static MemoryHitDiagnostics MemoryDiagnostics(IEnumerable<ExecutionEvent> events)
    {
        var decisions = events
            .Where(item => item.Type == ExecutionEventTypes.ResolutionConsidered)
            .Select(item => new
            {
                Resolver = ReadString(item.Data, "resolver") ?? "unknown",
                Accepted = ReadBoolean(item.Data, "accepted"),
                Code = ReadString(item.Data, "code") ?? "unknown"
            })
            .Where(item => MemoryResolvers.Contains(item.Resolver))
            .ToArray();
        var resolvers = decisions
            .GroupBy(item => item.Resolver, StringComparer.Ordinal)
            .OrderBy(item => item.Key, StringComparer.Ordinal)
            .Select(group => new MemoryResolverDiagnostic(
                group.Key,
                group.Count(),
                group.Count(item => item.Accepted),
                group.Count(item => !item.Accepted),
                group.GroupBy(item => item.Code, StringComparer.Ordinal)
                    .OrderBy(item => item.Key, StringComparer.Ordinal)
                    .ToDictionary(item => item.Key, item => item.Count(), StringComparer.Ordinal)))
            .ToArray();
        var accepted = decisions.Count(item => item.Accepted);
        return new MemoryHitDiagnostics(
            decisions.Length,
            accepted,
            decisions.Length - accepted,
            Ratio(accepted, decisions.Length),
            resolvers);
    }

    private static GatewayObservabilityMetricGroup[] GatewayGroups(
        IEnumerable<ExecutionEvent> events)
    {
        var observations = events.Select(item => item.Type switch
        {
            ExecutionEventTypes.GatewayAttempted =>
                ReadGatewayObservation(item.Data, attempted: true),
            ExecutionEventTypes.GatewayCandidateEvaluated when !ReadBoolean(item.Data, "eligible") =>
                ReadGatewayObservation(item.Data, attempted: false),
            _ => null
        }).Where(item => item is not null).Cast<GatewayObservation>().ToArray();
        return observations
            .GroupBy(item => new
            {
                item.GatewayId,
                item.Provider,
                item.DeploymentId,
                item.Region,
                item.ModelVersion,
                item.FailureClass,
                item.CircuitState
            })
            .OrderBy(item => item.Key.GatewayId, StringComparer.Ordinal)
            .ThenBy(item => item.Key.Provider, StringComparer.Ordinal)
            .ThenBy(item => item.Key.DeploymentId, StringComparer.Ordinal)
            .ThenBy(item => item.Key.Region, StringComparer.Ordinal)
            .ThenBy(item => item.Key.ModelVersion, StringComparer.Ordinal)
            .ThenBy(item => item.Key.FailureClass)
            .ThenBy(item => item.Key.CircuitState)
            .Select(group => new GatewayObservabilityMetricGroup(
                group.Key.GatewayId,
                group.Key.Provider,
                group.Key.DeploymentId,
                group.Key.Region,
                group.Key.ModelVersion,
                group.Key.FailureClass,
                group.Key.CircuitState,
                new GatewayObservabilityMetricSet(
                    group.Count(item => item.Attempted),
                    group.Count(item => item.Attempted && item.Succeeded),
                    group.Count(item => item.Attempted && !item.Succeeded),
                    group.Count(item => !item.Attempted),
                    group.Count(item => item.Attempted && item.FallbackSelected),
                    group.Any(item => item.Attempted)
                        ? (decimal)group.Where(item => item.Attempted).Average(item => item.DurationMilliseconds)
                        : 0m)))
            .ToArray();
    }

    private static GatewayObservation? ReadGatewayObservation(JsonElement data, bool attempted)
    {
        var gatewayId = ReadString(data, "gatewayId");
        var provider = ReadString(data, "provider");
        var deploymentId = ReadString(data, "deploymentId");
        var region = ReadString(data, "region");
        var modelVersion = ReadString(data, "modelVersion");
        if (gatewayId is null || provider is null || deploymentId is null || region is null || modelVersion is null)
        {
            return null;
        }

        return new GatewayObservation(
            gatewayId,
            provider,
            deploymentId,
            region,
            modelVersion,
            ReadEnum<GatewayFailureClass>(data, "failureClass"),
            ReadEnum<GatewayCircuitState>(data, attempted ? "circuitStateBefore" : "circuitState") ??
                GatewayCircuitState.Closed,
            attempted,
            attempted && ReadBoolean(data, "succeeded"),
            ReadInt32(data, "attempt"),
            ReadInt64(data, "durationMilliseconds"),
            attempted &&
                (ReadBoolean(data, "fallbackSelected") || ReadInt32(data, "attempt") > 1));
    }

    private static ObservabilityExecutionItem RecentExecution(ExecutionObservation observation)
    {
        var outcome = observation.Snapshot.Outcome;
        return new ObservabilityExecutionItem(
            observation.Snapshot.ExecutionId,
            observation.Snapshot.TaskType,
            observation.PolicyVersion,
            observation.Snapshot.Status,
            observation.Snapshot.CreatedAt,
            Math.Max(0, (long)(observation.Snapshot.UpdatedAt - observation.Snapshot.CreatedAt).TotalMilliseconds),
            outcome?.ResolutionLevel,
            outcome is null ? null : Quality(outcome),
            outcome?.Usage.Cost ?? 0,
            outcome?.Usage.InputTokens ?? 0,
            outcome?.Usage.OutputTokens ?? 0);
    }

    private static string PolicyVersion(
        ExecutionSnapshot snapshot,
        IReadOnlyList<ExecutionEvent> events) =>
        snapshot.Outcome?.Routing?.PolicyVersion ??
        events.Where(item => item.Type == ExecutionEventTypes.RoutingDecided)
            .Select(item => ReadString(item.Data, "policyVersion"))
            .FirstOrDefault(item => !string.IsNullOrWhiteSpace(item)) ??
        (snapshot.Outcome is null ? "unrouted" : "resolution.v1");

    private static string TraceId(Guid executionId, IReadOnlyList<ExecutionEvent> events) =>
        events.Where(item => item.Type == ExecutionEventTypes.Created)
            .Select(item => ReadString(item.Data, "traceId"))
            .FirstOrDefault(item => !string.IsNullOrWhiteSpace(item)) ??
        executionId.ToString("N");

    private static JsonElement Redact(JsonElement data, ObservabilityOptions options)
    {
        if (options.PayloadMode == ObservabilityPayloadMode.MetadataOnly)
        {
            return JsonSerializer.SerializeToElement(new { redacted = true });
        }

        var node = JsonNode.Parse(data.GetRawText());
        return JsonSerializer.SerializeToElement(RedactNode(node, null, options.MaxEventValueCharacters));
    }

    private static JsonNode? RedactNode(JsonNode? node, string? property, int maximumCharacters)
    {
        if (property is not null && IsRedactedProperty(property))
        {
            return JsonValue.Create("[REDACTED]");
        }

        if (node is JsonObject valueObject)
        {
            var redactedObject = new JsonObject();
            foreach (var item in valueObject)
            {
                redactedObject[item.Key] = RedactNode(item.Value, item.Key, maximumCharacters);
            }

            return redactedObject;
        }

        if (node is JsonArray array)
        {
            var redactedArray = new JsonArray();
            foreach (var item in array)
            {
                redactedArray.Add(RedactNode(item, null, maximumCharacters));
            }

            return redactedArray;
        }

        if (node is JsonValue value &&
            value.TryGetValue<string>(out var text) &&
            text.Length > maximumCharacters)
        {
            return JsonValue.Create($"{text[..maximumCharacters]}…[TRUNCATED]");
        }

        return node?.DeepClone();
    }

    private static bool IsRedactedProperty(string property)
    {
        if (RedactedProperties.Contains(property))
        {
            return true;
        }

        var normalized = new string(property
            .Where(char.IsLetterOrDigit)
            .Select(char.ToLowerInvariant)
            .ToArray());
        return normalized.EndsWith("permissionscopes", StringComparison.Ordinal) ||
               normalized.EndsWith("authenticationscopes", StringComparison.Ordinal) ||
               normalized.EndsWith("password", StringComparison.Ordinal) ||
               normalized.EndsWith("secret", StringComparison.Ordinal) ||
               normalized.EndsWith("apikey", StringComparison.Ordinal) ||
               normalized.EndsWith("token", StringComparison.Ordinal) ||
               normalized.EndsWith("cookie", StringComparison.Ordinal) ||
               normalized.Contains("authorization", StringComparison.Ordinal);
    }

    private static string? ReadString(JsonElement data, string name)
    {
        if (!TryGetProperty(data, name, out var value) || value.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        return value.GetString();
    }

    private static bool ReadBoolean(JsonElement data, string name) =>
        TryGetProperty(data, name, out var value) && value.ValueKind == JsonValueKind.True;

    private static int ReadInt32(JsonElement data, string name) =>
        TryGetProperty(data, name, out var value) && value.TryGetInt32(out var result) ? result : 0;

    private static long ReadInt64(JsonElement data, string name) =>
        TryGetProperty(data, name, out var value) && value.TryGetInt64(out var result) ? result : 0;

    private static T? ReadEnum<T>(JsonElement data, string name) where T : struct, Enum
    {
        var value = ReadString(data, name);
        return Enum.TryParse<T>(value, true, out var parsed) ? parsed : null;
    }

    private static bool TryGetProperty(JsonElement data, string name, out JsonElement value)
    {
        if (data.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in data.EnumerateObject())
            {
                if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
                {
                    value = property.Value;
                    return true;
                }
            }
        }

        value = default;
        return false;
    }

    private static decimal Ratio(int numerator, int denominator) =>
        denominator == 0 ? 0 : (decimal)numerator / denominator;

    private static bool IsNoModel(ResolutionLevel level) => level is not
        (ResolutionLevel.SmallModel or ResolutionLevel.StrongModel or ResolutionLevel.VerifiedOrHuman);

    private static decimal Quality(TaskOutcome outcome) =>
        outcome.Validation?.Quality ?? outcome.Confidence;

    private static string HashReference(string value)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return $"sha256:{Convert.ToHexStringLower(hash)[..16]}";
    }

    private sealed record ExecutionObservation(
        ExecutionSnapshot Snapshot,
        IReadOnlyList<ExecutionEvent> Events,
        string PolicyVersion);

    private sealed record GatewayObservation(
        string GatewayId,
        string Provider,
        string DeploymentId,
        string Region,
        string ModelVersion,
        GatewayFailureClass? FailureClass,
        GatewayCircuitState CircuitState,
        bool Attempted,
        bool Succeeded,
        int Attempt,
        long DurationMilliseconds,
        bool FallbackSelected);
}
