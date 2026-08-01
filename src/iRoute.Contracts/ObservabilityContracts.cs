using System.Text.Json;
using System.Text.Json.Serialization;

namespace iRoute.Contracts;

public sealed record ObservabilitySummary(
    DateTimeOffset From,
    DateTimeOffset To,
    bool Truncated,
    int SampledExecutions,
    ObservabilityMetricSet Totals,
    IReadOnlyList<ObservabilityMetricGroup> Groups,
    MemoryHitDiagnostics Memory,
    IReadOnlyList<ObservabilityExecutionItem> RecentExecutions);

public sealed record ObservabilityExecutionItem(
    Guid ExecutionId,
    string TaskType,
    string PolicyVersion,
    ExecutionStatus Status,
    DateTimeOffset StartedAt,
    long DurationMilliseconds,
    ResolutionLevel? ResolutionLevel,
    decimal? Quality,
    decimal Cost,
    int InputTokens,
    int OutputTokens);

public sealed record ObservabilityMetricSet(
    int Executions,
    int Completed,
    int Failed,
    decimal CompletionRate,
    decimal AverageQuality,
    long InputTokens,
    long OutputTokens,
    decimal Cost,
    decimal AverageLatencyMilliseconds,
    int ModelCalls,
    int ToolCalls,
    int NoModelResolutions,
    int ModelCallsAvoided);

public sealed record ObservabilityMetricGroup(
    string TaskType,
    string PolicyVersion,
    ObservabilityMetricSet Metrics);

public sealed record MemoryHitDiagnostics(
    int Considered,
    int Accepted,
    int Rejected,
    decimal HitRate,
    IReadOnlyList<MemoryResolverDiagnostic> Resolvers);

public sealed record MemoryResolverDiagnostic(
    string Resolver,
    int Considered,
    int Accepted,
    int Rejected,
    IReadOnlyDictionary<string, int> Reasons);

public sealed record ExecutionTimeline(
    Guid ExecutionId,
    string TraceId,
    string TaskType,
    int? TaskDefinitionVersion,
    string PolicyVersion,
    ExecutionStatus Status,
    DateTimeOffset StartedAt,
    DateTimeOffset UpdatedAt,
    long DurationMilliseconds,
    string ActorReference,
    string? ProjectReference,
    ObservabilityPayloadMode PayloadMode,
    bool Truncated,
    IReadOnlyList<ExecutionTimelineEntry> Events);

public sealed record ExecutionTimelineEntry(
    long Sequence,
    string Type,
    DateTimeOffset OccurredAt,
    long OffsetMilliseconds,
    JsonElement Data);

[JsonConverter(typeof(JsonStringEnumConverter<ObservabilityPayloadMode>))]
public enum ObservabilityPayloadMode
{
    MetadataOnly,
    Redacted
}
