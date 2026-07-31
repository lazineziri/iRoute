using System.Text.Json;
using iRoute.Contracts;

namespace iRoute.Core;

public interface IExecutionStore
{
    Task<ExecutionSnapshot?> FindByIdempotencyKeyAsync(
        string tenantId,
        string key,
        CancellationToken cancellationToken);
    Task<ExecutionSnapshot?> GetAsync(Guid executionId, CancellationToken cancellationToken);
    Task CreateAsync(ExecutionSnapshot execution, string? idempotencyKey, CancellationToken cancellationToken);
    Task UpdateAsync(ExecutionSnapshot execution, CancellationToken cancellationToken);
    IAsyncEnumerable<ExecutionEvent> ReadEventsAsync(Guid executionId, long afterSequence, CancellationToken cancellationToken);
    Task<ExecutionEvent> AppendEventAsync(
        Guid executionId,
        string eventType,
        DateTimeOffset occurredAt,
        JsonElement data,
        CancellationToken cancellationToken);
}

public interface IArtifactStore
{
    Task<ArtifactRecord?> FindReusableAsync(
        ArtifactReuseQuery query,
        CancellationToken cancellationToken);
    Task<ArtifactRecord?> GetAsync(Guid artifactId, CancellationToken cancellationToken);
    Task<ArtifactRecord> SaveAsync(ArtifactRecord artifact, CancellationToken cancellationToken);
}

public interface INoModelResolver
{
    int Order { get; }
    Task<ResolutionCandidate?> TryResolveAsync(TaskRequest request, CancellationToken cancellationToken);
}

public interface IModelGateway
{
    Task<ModelGatewayResult> ExecuteAsync(ModelGatewayRequest request, CancellationToken cancellationToken);
}

public sealed class ModelGatewayException(
    string code,
    string message,
    bool retryable,
    int? statusCode = null,
    Exception? innerException = null) : Exception(message, innerException)
{
    public string Code { get; } = code;
    public bool Retryable { get; } = retryable;
    public int? StatusCode { get; } = statusCode;
}

public interface IContextCompiler
{
    Task<CompiledContext> CompileAsync(
        TaskRequest request,
        TaskDefinition definition,
        CancellationToken cancellationToken);
}

public interface ITaskOutcomeValidator
{
    bool Supports(string taskType);
    Task<OutcomeValidationResult> ValidateAsync(
        TaskRequest request,
        TaskDefinition definition,
        ModelGatewayResult result,
        CompiledContext context,
        CancellationToken cancellationToken);
}

public interface IInputFingerprint
{
    string Create(TaskRequest request, int taskDefinitionVersion);
}

public interface IExecutionCancellationRegistry
{
    CancellationToken Register(Guid executionId, CancellationToken requestCancellation);
    bool RequestCancellation(Guid executionId);
    void Complete(Guid executionId);
}

public interface ITaskDefinitionRegistry
{
    Task<TaskDefinition?> FindAsync(string taskType, CancellationToken cancellationToken);
}

public interface IExecutionPlanFactory
{
    ExecutionPlan Create(TaskRequest request, TaskDefinition definition);
}

public interface IExecutionPlanValidator
{
    ExecutionPlanValidationResult Validate(ExecutionPlan plan);
    void EnsureValid(ExecutionPlan plan);
}

public interface IClock
{
    DateTimeOffset UtcNow { get; }
}

public sealed record ResolutionCandidate(
    ResolutionLevel Level,
    JsonElement Output,
    decimal Confidence,
    IReadOnlyList<EvidenceReference> Evidence,
    bool IsFresh,
    ArtifactReference? Artifact = null);

public sealed record ModelGatewayRequest(
    string Capability,
    JsonElement Input,
    JsonElement Context,
    int MaxOutputTokens,
    string? CorrelationId = null);

public sealed record ModelGatewayResult(
    JsonElement Output,
    UsageSummary Usage,
    decimal Confidence,
    IReadOnlyList<EvidenceReference> Evidence);

public sealed record CompiledContext(
    JsonElement Content,
    ContextManifest Manifest,
    IReadOnlyList<EvidenceReference> Evidence);

public sealed record OutcomeValidationResult(
    bool Passed,
    decimal Quality,
    IReadOnlyList<string> Checks,
    IReadOnlyList<string> Failures)
{
    public ValidationSummary ToContract() => new(Passed, Quality, Checks, Failures);
}

public sealed record ArtifactReuseQuery(
    string TenantId,
    string? ProjectId,
    string TaskType,
    int TaskDefinitionVersion,
    string InputHash,
    DateTimeOffset At);

public sealed record ArtifactRecord(
    Guid ArtifactId,
    string TenantId,
    string? ProjectId,
    string TaskType,
    int TaskDefinitionVersion,
    string ArtifactType,
    int Version,
    string InputHash,
    string ContentHash,
    JsonElement Content,
    IReadOnlyList<EvidenceReference> Evidence,
    DateTimeOffset CreatedAt,
    DateTimeOffset? ExpiresAt,
    bool IsActive)
{
    public ArtifactReference ToReference() => new(ArtifactId, ArtifactType, Version, ContentHash);

    public ArtifactSnapshot ToSnapshot() => new(
        ToReference(),
        TenantId,
        ProjectId,
        TaskType,
        TaskDefinitionVersion,
        Content,
        Evidence,
        CreatedAt,
        ExpiresAt,
        IsActive);
}

public sealed record TaskDefinition(
    string TaskType,
    int Version,
    string Capability,
    int DefaultMaxOutputTokens,
    decimal MinimumQuality,
    bool RequiresEvidence,
    SideEffectClass SideEffectClass,
    string ArtifactType,
    int DefaultMaxInputTokens = 4000,
    int DefaultDeadlineMilliseconds = 30000,
    int DefaultMaxModelCalls = 1,
    TimeSpan? ArtifactTimeToLive = null);
