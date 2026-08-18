using iRoute.Common;

namespace iRoute.Data;

public sealed class ExecutionEntity
{
    public Guid ExecutionId { get; set; }
    public string TenantId { get; set; } = null!;
    public string ActorId { get; set; } = null!;
    public string? ProjectId { get; set; }
    public string TaskType { get; set; } = null!;
    public int? TaskDefinitionVersion { get; set; }
    public ExecutionStatus Status { get; set; }
    public long CreatedAtUnixMilliseconds { get; set; }
    public long UpdatedAtUnixMilliseconds { get; set; }
    public long? CancellationRequestedAtUnixMilliseconds { get; set; }
    public string? IdempotencyKey { get; set; }
    public string? InputFingerprint { get; set; }
    public string? OutcomeJson { get; set; }
    public string? ErrorJson { get; set; }
}

public sealed class ExecutionEventEntity
{
    public Guid ExecutionId { get; set; }
    public long Sequence { get; set; }
    public string EventType { get; set; } = null!;
    public long OccurredAtUnixMilliseconds { get; set; }
    public string DataJson { get; set; } = null!;
}

public sealed class ExecutionWorkItemEntity
{
    public Guid ExecutionId { get; set; }
    public ExecutionWorkState State { get; set; }
    public long AvailableAtUnixMilliseconds { get; set; }
    public int DeliveryAttempt { get; set; }
    public string? LeaseOwner { get; set; }
    public Guid? LeaseToken { get; set; }
    public long? LeaseExpiresAtUnixMilliseconds { get; set; }
    public long? HeartbeatAtUnixMilliseconds { get; set; }
    public long? CompletedAtUnixMilliseconds { get; set; }
}

public sealed class GatewayCircuitEntity
{
    public string DeploymentId { get; set; } = null!;
    public GatewayCircuitState State { get; set; }
    public int ConsecutiveFailures { get; set; }
    public int OpenCount { get; set; }
    public long? OpenedAtUnixMilliseconds { get; set; }
    public long? NextProbeAtUnixMilliseconds { get; set; }
    public string? ProbeOwner { get; set; }
    public Guid? ProbeToken { get; set; }
    public long? ProbeLeaseExpiresAtUnixMilliseconds { get; set; }
    public GatewayFailureClass? LastFailureClass { get; set; }
    public long? LastFailureAtUnixMilliseconds { get; set; }
    public long UpdatedAtUnixMilliseconds { get; set; }
}

public sealed class WorkflowPlanEntity
{
    public Guid ExecutionId { get; set; }
    public string RequestJson { get; set; } = null!;
    public string PlanJson { get; set; } = null!;
    public string RoutingJson { get; set; } = null!;
    public long CreatedAtUnixMilliseconds { get; set; }
    public long UpdatedAtUnixMilliseconds { get; set; }
}

public sealed class WorkflowStepEntity
{
    public Guid ExecutionId { get; set; }
    public string StepId { get; set; } = null!;
    public WorkflowStepStatus Status { get; set; }
    public int Attempt { get; set; }
    public long? StartedAtUnixMilliseconds { get; set; }
    public long? CompletedAtUnixMilliseconds { get; set; }
    public string? OutputJson { get; set; }
    public string? ErrorJson { get; set; }
}

public sealed class ArtifactEntity
{
    public Guid ArtifactId { get; set; }
    public string TenantId { get; set; } = null!;
    public string ProjectId { get; set; } = string.Empty;
    public string TaskType { get; set; } = null!;
    public int TaskDefinitionVersion { get; set; }
    public string ArtifactType { get; set; } = null!;
    public int Version { get; set; }
    public string InputHash { get; set; } = null!;
    public string ContentHash { get; set; } = null!;
    public string ContentJson { get; set; } = null!;
    public string EvidenceJson { get; set; } = null!;
    public long CreatedAtUnixMilliseconds { get; set; }
    public long? ExpiresAtUnixMilliseconds { get; set; }
    public bool IsActive { get; set; }
    public string LogicalKey { get; set; } = null!;
    public ArtifactLifecycleStatus LifecycleStatus { get; set; }
    public Guid? SupersedesArtifactId { get; set; }
    public Guid? SupersededByArtifactId { get; set; }
    public long? InvalidatedAtUnixMilliseconds { get; set; }
    public string? InvalidationReason { get; set; }
}
