using System.Text.Json;
using iRoute.Contracts;

namespace iRoute.Core;

public enum PolicyDecisionKind
{
    Allowed,
    ApprovalRequired,
    Denied
}

public sealed record PolicyEvaluation(
    string PolicyVersion,
    PolicyDecisionKind Decision,
    string Capability,
    SideEffectClass SideEffectClass,
    IReadOnlyList<string> RequiredPermissionScopes,
    IReadOnlyList<string> MissingPermissionScopes,
    string? Code = null,
    string? Reason = null);

public interface ITaskPolicyEngine
{
    PolicyEvaluation Evaluate(
        TaskRequest request,
        TaskDefinition definition,
        ExecutionPlan plan,
        ApprovalRecord? approval = null);

    PolicyEvaluation EvaluateApproval(
        ApprovalRecord approval,
        IReadOnlyCollection<string> approverPermissionScopes);
}

public sealed record ApprovalRecord(
    Guid ExecutionId,
    string TenantId,
    string ActionId,
    ApprovalStatus Status,
    string Capability,
    SideEffectClass SideEffectClass,
    IReadOnlyList<string> RequiredPermissionScopes,
    string RequestedByActorId,
    string? DecidedByActorId,
    string InputReference,
    string IdempotencyReference,
    DateTimeOffset CreatedAt,
    DateTimeOffset? DecidedAt = null,
    string? Reason = null)
{
    public ApprovalSnapshot ToSnapshot() => new(
        ExecutionId,
        ActionId,
        Status,
        Capability,
        SideEffectClass,
        RequiredPermissionScopes,
        RequestedByActorId,
        DecidedByActorId,
        InputReference,
        IdempotencyReference,
        CreatedAt,
        DecidedAt,
        Reason);
}

public sealed record ApprovalDecisionResult(ApprovalRecord Approval, bool Applied);

public interface IApprovalStore
{
    Task<ApprovalRecord> CreatePendingAsync(
        ApprovalRecord approval,
        CancellationToken cancellationToken);

    Task<ApprovalRecord?> GetAsync(
        Guid executionId,
        string actionId,
        CancellationToken cancellationToken);

    Task<ApprovalDecisionResult> DecideAsync(
        Guid executionId,
        string actionId,
        bool approved,
        string actorId,
        string? reason,
        DateTimeOffset decidedAt,
        CancellationToken cancellationToken);
}

public enum ExternalActionStatus
{
    Running,
    Succeeded,
    Failed
}

public sealed record ExternalActionResult(
    JsonElement Output,
    IReadOnlyList<EvidenceReference> Evidence);

public sealed record ExternalActionRecord(
    Guid ExecutionId,
    string TenantId,
    string ActionId,
    string Capability,
    string IdempotencyReference,
    string InputReference,
    ExternalActionStatus Status,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    ExternalActionResult? Result = null,
    Problem? Error = null);

public enum ExternalActionReservationKind
{
    Acquired,
    Reused,
    InProgress,
    Conflict,
    Failed
}

public sealed record ExternalActionReservation(
    ExternalActionReservationKind Kind,
    ExternalActionRecord Action);

public interface IExternalActionStore
{
    Task<ExternalActionReservation> ReserveAsync(
        ExternalActionRecord action,
        CancellationToken cancellationToken);

    Task<ExternalActionRecord> CompleteAsync(
        string tenantId,
        string idempotencyReference,
        ExternalActionResult result,
        DateTimeOffset completedAt,
        CancellationToken cancellationToken);

    Task<ExternalActionRecord> FailAsync(
        string tenantId,
        string idempotencyReference,
        Problem problem,
        DateTimeOffset failedAt,
        CancellationToken cancellationToken);
}

public sealed record ExternalActionRequest(
    Guid ExecutionId,
    string ActionId,
    string Capability,
    JsonElement Input,
    string IdempotencyKey,
    string TenantId = "local",
    string ActorId = "local",
    string? ProjectId = null,
    IReadOnlyList<string>? PermissionScopes = null,
    string PolicyVersion = "policy.unspecified",
    SideEffectClass SideEffectClass = SideEffectClass.IrreversibleWrite,
    int DeadlineMilliseconds = 30000);

public interface IExternalActionExecutor
{
    Task<ExternalActionResult> ExecuteAsync(
        ExternalActionRequest request,
        CancellationToken cancellationToken);
}
