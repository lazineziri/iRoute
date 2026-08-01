using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Security.Cryptography;
using System.Text;
using iRoute.Contracts;
using iRoute.Core;

namespace iRoute.Runtime;

public sealed class RuntimeTelemetry : IExecutionTelemetry, IDisposable
{
    public const string ActivitySourceName = "iRoute.Runtime";
    public const string MeterName = "iRoute.Runtime";

    private readonly ActivitySource _activities = new(ActivitySourceName);
    private readonly Meter _meter = new(MeterName);
    private readonly Counter<long> _started;
    private readonly Counter<long> _completed;
    private readonly Counter<long> _failed;
    private readonly Counter<long> _memoryHits;
    private readonly Counter<long> _modelCallsAvoided;
    private readonly Histogram<double> _quality;
    private readonly Histogram<double> _cost;
    private readonly Histogram<long> _inputTokens;
    private readonly Histogram<long> _outputTokens;
    private readonly Histogram<double> _duration;
    private bool _disposed;

    public RuntimeTelemetry()
    {
        _started = _meter.CreateCounter<long>("iroute.executions.started");
        _completed = _meter.CreateCounter<long>("iroute.executions.completed");
        _failed = _meter.CreateCounter<long>("iroute.executions.failed");
        _memoryHits = _meter.CreateCounter<long>("iroute.memory.hits");
        _modelCallsAvoided = _meter.CreateCounter<long>("iroute.model.calls.avoided");
        _quality = _meter.CreateHistogram<double>("iroute.execution.quality");
        _cost = _meter.CreateHistogram<double>("iroute.execution.cost");
        _inputTokens = _meter.CreateHistogram<long>("iroute.execution.input_tokens");
        _outputTokens = _meter.CreateHistogram<long>("iroute.execution.output_tokens");
        _duration = _meter.CreateHistogram<double>("iroute.execution.duration", "ms");
    }

    public IExecutionTraceScope StartExecution(
        ExecutionSnapshot snapshot,
        IReadOnlyCollection<string> permissionScopes,
        string operation)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var activity = _activities.StartActivity(
            $"iRoute.execution.{operation}",
            ActivityKind.Internal);
        activity?.SetTag("iroute.execution.id", snapshot.ExecutionId.ToString());
        activity?.SetTag("iroute.task.type", snapshot.TaskType);
        activity?.SetTag("iroute.tenant.ref", HashReference(snapshot.TenantId));
        activity?.SetTag("iroute.actor.ref", HashReference(snapshot.ActorId));
        activity?.SetTag("iroute.project.ref", HashReference(snapshot.ProjectId));
        activity?.SetTag("iroute.permission_scope.count", permissionScopes.Count);
        activity?.SetTag("iroute.operation", operation);
        _started.Add(1, new KeyValuePair<string, object?>("iroute.task.type", snapshot.TaskType));
        return new ExecutionTraceScope(
            activity,
            activity?.TraceId.ToHexString() ?? snapshot.ExecutionId.ToString("N"));
    }

    public void RecordEvent(string eventType)
    {
        Activity.Current?.AddEvent(new ActivityEvent(
            eventType,
            tags: new ActivityTagsCollection
            {
                { "iroute.event.type", eventType }
            }));
    }

    public void RecordTerminal(ExecutionSnapshot snapshot)
    {
        var outcome = snapshot.Outcome;
        var policyVersion = outcome?.Routing?.PolicyVersion ??
            (outcome is null ? "unrouted" : "resolution.v1");
        var tags = new TagList
        {
            { "iroute.task.type", snapshot.TaskType },
            { "iroute.policy.version", policyVersion },
            { "iroute.execution.status", snapshot.Status.ToString() }
        };
        var duration = Math.Max(0, (snapshot.UpdatedAt - snapshot.CreatedAt).TotalMilliseconds);
        _duration.Record(duration, tags);
        if (snapshot.Status == ExecutionStatus.Succeeded && outcome is not null)
        {
            var quality = outcome.Validation?.Quality ?? outcome.Confidence;
            _completed.Add(1, tags);
            _quality.Record((double)quality, tags);
            _cost.Record((double)outcome.Usage.Cost, tags);
            _inputTokens.Record(outcome.Usage.InputTokens, tags);
            _outputTokens.Record(outcome.Usage.OutputTokens, tags);
            if (IsMemoryHit(outcome.ResolutionLevel))
            {
                _memoryHits.Add(1, tags);
            }

            if (IsNoModel(outcome.ResolutionLevel))
            {
                _modelCallsAvoided.Add(1, tags);
            }
        }
        else
        {
            _failed.Add(1, tags);
        }

        var activity = Activity.Current;
        activity?.SetTag("iroute.task.version", snapshot.TaskDefinitionVersion);
        activity?.SetTag("iroute.policy.version", policyVersion);
        activity?.SetTag("iroute.execution.status", snapshot.Status.ToString());
        activity?.SetTag("iroute.execution.duration_ms", duration);
        activity?.SetTag("iroute.resolution.level", outcome?.ResolutionLevel.ToString());
        activity?.SetTag("iroute.quality", outcome is null
            ? null
            : outcome.Validation?.Quality ?? outcome.Confidence);
        activity?.SetTag("iroute.cost", outcome?.Usage.Cost);
        activity?.SetTag("iroute.input_tokens", outcome?.Usage.InputTokens);
        activity?.SetTag("iroute.output_tokens", outcome?.Usage.OutputTokens);
        activity?.SetTag("iroute.model_calls", outcome?.Usage.ModelCalls);
        activity?.SetTag("iroute.tool_calls", outcome?.Usage.ToolCalls);
        activity?.SetStatus(
            snapshot.Status == ExecutionStatus.Succeeded
                ? ActivityStatusCode.Ok
                : ActivityStatusCode.Error,
            snapshot.Error?.Code);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _activities.Dispose();
        _meter.Dispose();
        _disposed = true;
    }

    private static bool IsMemoryHit(ResolutionLevel level) => level is
        ResolutionLevel.ExactArtifact or
        ResolutionLevel.StructuredState or
        ResolutionLevel.SemanticMemory;

    private static bool IsNoModel(ResolutionLevel level) => level is not
        (ResolutionLevel.SmallModel or ResolutionLevel.StrongModel or ResolutionLevel.VerifiedOrHuman);

    private static string? HashReference(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return $"sha256:{Convert.ToHexStringLower(hash)[..16]}";
    }

    private sealed class ExecutionTraceScope(Activity? activity, string traceId) : IExecutionTraceScope
    {
        public string TraceId { get; } = traceId;
        public void Dispose() => activity?.Dispose();
    }
}
