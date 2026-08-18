using iRoute.Contracts;

namespace iRoute.Core;

public sealed record ObservabilityOptions
{
    public ObservabilityPayloadMode PayloadMode { get; init; } = ObservabilityPayloadMode.MetadataOnly;
    public int MaxQueryDays { get; init; } = 90;
    public int MaxExecutions { get; init; } = 1_000;
    public int MaxTimelineEvents { get; init; } = 1_000;
    public int MaxEventValueCharacters { get; init; } = 1_000;
    public int MaxRecentExecutions { get; init; } = 25;

    public void EnsureValid()
    {
        if (MaxQueryDays < 1 ||
            MaxExecutions < 1 ||
            MaxTimelineEvents < 1 ||
            MaxEventValueCharacters < 1 ||
            MaxRecentExecutions < 1)
        {
            throw new InvalidOperationException("Observability query and timeline bounds must be positive.");
        }
    }
}

public sealed record ObservabilityQuery(
    string TenantId,
    DateTimeOffset From,
    DateTimeOffset To,
    string? TaskType = null,
    string? PolicyVersion = null);

public interface IObservabilityStore
{
    Task<ObservabilitySummary> QueryAsync(
        ObservabilityQuery query,
        CancellationToken cancellationToken);

    Task<ExecutionTimeline?> GetTimelineAsync(
        string tenantId,
        Guid executionId,
        CancellationToken cancellationToken);
}

public interface IExecutionTelemetry
{
    IExecutionTraceScope StartExecution(
        ExecutionSnapshot snapshot,
        IReadOnlyCollection<string> permissionScopes,
        string operation);

    void RecordEvent(string eventType);
    void RecordGatewayAttempt(GatewayAttemptEvidence attempt, bool fallbackSelected);
    void RecordTerminal(ExecutionSnapshot snapshot);
}

public interface IExecutionTraceScope : IDisposable
{
    string TraceId { get; }
}

public sealed class NoOpExecutionTelemetry : IExecutionTelemetry
{
    public static NoOpExecutionTelemetry Instance { get; } = new();

    private NoOpExecutionTelemetry()
    {
    }

    public IExecutionTraceScope StartExecution(
        ExecutionSnapshot snapshot,
        IReadOnlyCollection<string> permissionScopes,
        string operation) => new NoOpTraceScope(snapshot.ExecutionId.ToString("N"));

    public void RecordEvent(string eventType)
    {
    }

    public void RecordGatewayAttempt(GatewayAttemptEvidence attempt, bool fallbackSelected)
    {
    }

    public void RecordTerminal(ExecutionSnapshot snapshot)
    {
    }

    private sealed class NoOpTraceScope(string traceId) : IExecutionTraceScope
    {
        public string TraceId { get; } = traceId;
        public void Dispose()
        {
        }
    }
}
