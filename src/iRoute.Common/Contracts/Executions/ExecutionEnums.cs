using System.Text.Json.Serialization;

namespace iRoute.Common;

[JsonConverter(typeof(JsonStringEnumConverter<ExecutionStatus>))]
public enum ExecutionStatus
{
    Accepted,
    Resolving,
    Planning,
    Queued,
    WaitingForApproval,
    Running,
    Validating,
    Materializing,
    Compensating,
    Succeeded,
    Failed,
    Cancelled,
    TimedOut
}

[JsonConverter(typeof(JsonStringEnumConverter<ResolutionLevel>))]
public enum ResolutionLevel
{
    ExactArtifact,
    StructuredState,
    SemanticMemory,
    DeterministicCapability,
    SmallModel,
    StrongModel,
    VerifiedOrHuman
}

[JsonConverter(typeof(JsonStringEnumConverter<ExecutionStepKind>))]
public enum ExecutionStepKind
{
    Deterministic,
    Model,
    Tool,
    Approval
}

[JsonConverter(typeof(JsonStringEnumConverter<RoutingPath>))]
public enum RoutingPath
{
    Direct,
    Workflow
}

[JsonConverter(typeof(JsonStringEnumConverter<ModelTier>))]
public enum ModelTier
{
    Small,
    Strong,
    Verifier
}

[JsonConverter(typeof(JsonStringEnumConverter<ModelProfileSource>))]
public enum ModelProfileSource
{
    Synthetic,
    Unverified,
    Measured
}

[JsonConverter(typeof(JsonStringEnumConverter<ModelGatewayTransport>))]
public enum ModelGatewayTransport
{
    Buffered,
    Streaming
}

[JsonConverter(typeof(JsonStringEnumConverter<ModelGatewayStreamEventKind>))]
public enum ModelGatewayStreamEventKind
{
    OutputDelta,
    Usage,
    Completed
}

[JsonConverter(typeof(JsonStringEnumConverter<ModelGatewayFinishReason>))]
public enum ModelGatewayFinishReason
{
    Completed,
    Length,
    ContentFiltered,
    ToolCall,
    Other
}

[JsonConverter(typeof(JsonStringEnumConverter<ModelGatewayHealthStatus>))]
public enum ModelGatewayHealthStatus
{
    Healthy,
    Degraded,
    Unavailable
}

[JsonConverter(typeof(JsonStringEnumConverter<ModelGatewayFailureKind>))]
public enum ModelGatewayFailureKind
{
    InvalidRequest,
    Authentication,
    RateLimited,
    Timeout,
    Unavailable,
    InvalidResponse,
    Cancelled,
    Internal
}

[JsonConverter(typeof(JsonStringEnumConverter<GatewayFailureClass>))]
public enum GatewayFailureClass
{
    Timeout,
    Throttling,
    Transport,
    Provider,
    MalformedOutput,
    Validation,
    Policy,
    Permanent
}

[JsonConverter(typeof(JsonStringEnumConverter<GatewayCircuitState>))]
public enum GatewayCircuitState
{
    Closed,
    Open,
    HalfOpen
}

[JsonConverter(typeof(JsonStringEnumConverter<SideEffectClass>))]
public enum SideEffectClass
{
    None,
    ReadOnly,
    ReversibleWrite,
    IrreversibleWrite
}

[JsonConverter(typeof(JsonStringEnumConverter<ApprovalStatus>))]
public enum ApprovalStatus
{
    Pending,
    Approved,
    Denied
}

[JsonConverter(typeof(JsonStringEnumConverter<ArtifactLifecycleStatus>))]
public enum ArtifactLifecycleStatus
{
    Active,
    Superseded,
    Invalidated
}

[JsonConverter(typeof(JsonStringEnumConverter<MemoryKind>))]
public enum MemoryKind
{
    Fact,
    Decision
}

[JsonConverter(typeof(JsonStringEnumConverter<MemoryLifecycleStatus>))]
public enum MemoryLifecycleStatus
{
    Active,
    Superseded,
    Invalidated
}
