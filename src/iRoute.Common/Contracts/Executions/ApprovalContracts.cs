using System.Text.Json;

namespace iRoute.Common;

public sealed record ApprovalDecision(
    string ActionId,
    bool Approved,
    string? Reason = null);

public sealed record ApprovalSnapshot(
    Guid ExecutionId,
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
    string? Reason = null);

public sealed record ApprovalResult(
    ApprovalSnapshot Approval,
    ExecutionSnapshot Execution);

public sealed record Problem(
    string Code,
    string Title,
    string Detail,
    bool Retryable = false,
    IReadOnlyDictionary<string, string>? Metadata = null);

public sealed record ExecutionEvent(
    long Sequence,
    Guid ExecutionId,
    string Type,
    DateTimeOffset OccurredAt,
    JsonElement Data);
