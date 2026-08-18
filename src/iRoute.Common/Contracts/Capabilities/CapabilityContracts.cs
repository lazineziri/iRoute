using System.Text.Json;
using System.Text.Json.Serialization;

namespace iRoute.Common;

public sealed record CapabilityDefinition(
    string Capability,
    int Version,
    CapabilityKind Kind,
    string Description,
    JsonElement InputSchema,
    JsonElement OutputSchema,
    SideEffectClass SideEffectClass,
    IReadOnlyList<string> AuthenticationScopes,
    IReadOnlyList<string> PermissionScopes,
    CapabilityIdempotencyPolicy Idempotency,
    CapabilityRetryPolicy Retry,
    CapabilityServiceProfile ServiceProfile,
    CapabilityCacheability Cacheability,
    int? FreshnessSeconds,
    CapabilityDataSensitivity DataSensitivity,
    CapabilityTrustLevel TrustLevel,
    CapabilityIsolation Isolation);

public sealed record CapabilityIdempotencyPolicy(
    bool Supported,
    bool RequiredForWrites);

public sealed record CapabilityRetryPolicy(
    int MaxAttempts,
    IReadOnlyList<string> RetryableFailureClasses);

public sealed record CapabilityServiceProfile(
    int ExpectedLatencyMilliseconds,
    decimal EstimatedCost,
    decimal Availability,
    decimal Reliability);

public sealed record CapabilityInvocationRequest(
    string Capability,
    int Version,
    JsonElement Input,
    string TenantId,
    string ActorId,
    string? ProjectId,
    IReadOnlyList<string> PermissionScopes,
    string PolicyVersion,
    SideEffectClass SideEffectClass,
    int DeadlineMilliseconds,
    int MaximumOutputBytes,
    string CorrelationId,
    string? IdempotencyReference = null);

public sealed record CapabilityInvocationResult(
    JsonElement Output,
    UsageSummary Usage,
    decimal Confidence,
    IReadOnlyList<EvidenceReference> Evidence,
    CapabilityExecutionMetadata Metadata);

public sealed record CapabilityExecutionMetadata(
    string Capability,
    int Version,
    string ConnectorId,
    CapabilityKind Kind,
    CapabilityTrustLevel TrustLevel,
    string Transport,
    bool Projected,
    string OutputReference);

public sealed record CapabilityFailure(
    string Code,
    CapabilityFailureKind Kind,
    string Message,
    bool Retryable,
    string? Capability = null,
    string? ConnectorId = null,
    string? CorrelationId = null);

[JsonConverter(typeof(JsonStringEnumConverter<CapabilityKind>))]
public enum CapabilityKind
{
    Deterministic,
    Model,
    Tool,
    Api,
    Mcp,
    Agent
}

[JsonConverter(typeof(JsonStringEnumConverter<CapabilityCacheability>))]
public enum CapabilityCacheability
{
    Never,
    Scoped,
    Public
}

[JsonConverter(typeof(JsonStringEnumConverter<CapabilityDataSensitivity>))]
public enum CapabilityDataSensitivity
{
    Public,
    Internal,
    Confidential,
    Restricted
}

[JsonConverter(typeof(JsonStringEnumConverter<CapabilityTrustLevel>))]
public enum CapabilityTrustLevel
{
    Internal,
    Registered,
    ExternalUntrusted
}

[JsonConverter(typeof(JsonStringEnumConverter<CapabilityIsolation>))]
public enum CapabilityIsolation
{
    InProcess,
    Process,
    Container,
    Remote
}

[JsonConverter(typeof(JsonStringEnumConverter<CapabilityFailureKind>))]
public enum CapabilityFailureKind
{
    InvalidRequest,
    PermissionDenied,
    NotRegistered,
    Timeout,
    Unavailable,
    InvalidResponse,
    OutputLimitExceeded,
    Cancelled,
    Internal
}
