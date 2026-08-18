using System.Data;
using System.Text.Json;
using iRoute.Common;
using Microsoft.EntityFrameworkCore;

namespace iRoute.Data;

public sealed class ApprovalEntity
{
    public Guid ExecutionId { get; set; }
    public string TenantId { get; set; } = null!;
    public string ActionId { get; set; } = null!;
    public ApprovalStatus Status { get; set; }
    public string Capability { get; set; } = null!;
    public SideEffectClass SideEffectClass { get; set; }
    public string RequiredPermissionScopesJson { get; set; } = null!;
    public string RequestedByActorId { get; set; } = null!;
    public string? DecidedByActorId { get; set; }
    public string InputReference { get; set; } = null!;
    public string IdempotencyReference { get; set; } = null!;
    public long CreatedAtUnixMilliseconds { get; set; }
    public long? DecidedAtUnixMilliseconds { get; set; }
    public string? Reason { get; set; }
}

public sealed class ExternalActionEntity
{
    public string TenantId { get; set; } = null!;
    public string IdempotencyReference { get; set; } = null!;
    public Guid ExecutionId { get; set; }
    public string ActionId { get; set; } = null!;
    public string Capability { get; set; } = null!;
    public string InputReference { get; set; } = null!;
    public ExternalActionStatus Status { get; set; }
    public string? ResultJson { get; set; }
    public string? ErrorJson { get; set; }
    public long CreatedAtUnixMilliseconds { get; set; }
    public long UpdatedAtUnixMilliseconds { get; set; }
}

public sealed class EfApprovalStore(IDbContextFactory<IRouteDbContext> contextFactory) : IApprovalStore
{
    public async Task<ApprovalRecord> CreatePendingAsync(
        ApprovalRecord approval,
        CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await context.Database.BeginTransactionAsync(
            IsolationLevel.Serializable,
            cancellationToken);
        var existing = await context.Approvals.SingleOrDefaultAsync(
            item => item.ExecutionId == approval.ExecutionId && item.ActionId == approval.ActionId,
            cancellationToken);
        if (existing is not null)
        {
            EnsureSameProposal(existing, approval);
            await transaction.CommitAsync(cancellationToken);
            return ToRecord(existing);
        }

        var entity = ToEntity(approval);
        context.Approvals.Add(entity);
        await context.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return ToRecord(entity);
    }

    public async Task<ApprovalRecord?> GetAsync(
        Guid executionId,
        string actionId,
        CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var entity = await context.Approvals
            .AsNoTracking()
            .SingleOrDefaultAsync(
                item => item.ExecutionId == executionId && item.ActionId == actionId,
                cancellationToken);
        return entity is null ? null : ToRecord(entity);
    }

    public async Task<ApprovalDecisionResult> DecideAsync(
        Guid executionId,
        string actionId,
        bool approved,
        string actorId,
        string? reason,
        DateTimeOffset decidedAt,
        CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await context.Database.BeginTransactionAsync(
            IsolationLevel.Serializable,
            cancellationToken);
        var entity = await context.Approvals.SingleOrDefaultAsync(
            item => item.ExecutionId == executionId && item.ActionId == actionId,
            cancellationToken) ?? throw new KeyNotFoundException(
                $"Approval '{actionId}' was not found for execution '{executionId}'.");
        var target = approved ? ApprovalStatus.Approved : ApprovalStatus.Denied;
        if (entity.Status != ApprovalStatus.Pending)
        {
            if (entity.Status != target)
            {
                throw new InvalidOperationException("The approval has already been decided differently.");
            }

            await transaction.CommitAsync(cancellationToken);
            return new ApprovalDecisionResult(ToRecord(entity), false);
        }

        entity.Status = target;
        entity.DecidedByActorId = actorId;
        entity.DecidedAtUnixMilliseconds = decidedAt.ToUnixTimeMilliseconds();
        entity.Reason = string.IsNullOrWhiteSpace(reason) ? null : reason.Trim();
        await context.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new ApprovalDecisionResult(ToRecord(entity), true);
    }

    private static ApprovalEntity ToEntity(ApprovalRecord approval) => new()
    {
        ExecutionId = approval.ExecutionId,
        TenantId = approval.TenantId,
        ActionId = approval.ActionId,
        Status = approval.Status,
        Capability = approval.Capability,
        SideEffectClass = approval.SideEffectClass,
        RequiredPermissionScopesJson = JsonSerializer.Serialize(approval.RequiredPermissionScopes),
        RequestedByActorId = approval.RequestedByActorId,
        DecidedByActorId = approval.DecidedByActorId,
        InputReference = approval.InputReference,
        IdempotencyReference = approval.IdempotencyReference,
        CreatedAtUnixMilliseconds = approval.CreatedAt.ToUnixTimeMilliseconds(),
        DecidedAtUnixMilliseconds = approval.DecidedAt?.ToUnixTimeMilliseconds(),
        Reason = approval.Reason
    };

    private static ApprovalRecord ToRecord(ApprovalEntity entity) => new(
        entity.ExecutionId,
        entity.TenantId,
        entity.ActionId,
        entity.Status,
        entity.Capability,
        entity.SideEffectClass,
        JsonSerializer.Deserialize<string[]>(entity.RequiredPermissionScopesJson) ?? [],
        entity.RequestedByActorId,
        entity.DecidedByActorId,
        entity.InputReference,
        entity.IdempotencyReference,
        DateTimeOffset.FromUnixTimeMilliseconds(entity.CreatedAtUnixMilliseconds),
        entity.DecidedAtUnixMilliseconds is { } decidedAt
            ? DateTimeOffset.FromUnixTimeMilliseconds(decidedAt)
            : null,
        entity.Reason);

    private static void EnsureSameProposal(ApprovalEntity existing, ApprovalRecord proposed)
    {
        if (!string.Equals(existing.TenantId, proposed.TenantId, StringComparison.Ordinal) ||
            !string.Equals(existing.Capability, proposed.Capability, StringComparison.Ordinal) ||
            existing.SideEffectClass != proposed.SideEffectClass ||
            !string.Equals(existing.InputReference, proposed.InputReference, StringComparison.Ordinal) ||
            !string.Equals(existing.IdempotencyReference, proposed.IdempotencyReference, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("A different approval proposal already uses this action identifier.");
        }
    }
}

public sealed class EfExternalActionStore(IDbContextFactory<IRouteDbContext> contextFactory)
    : IExternalActionStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public Task<ExternalActionReservation> ReserveAsync(
        ExternalActionRecord action,
        CancellationToken cancellationToken) =>
        PersistenceContention.RetryAsync(
            () => ReserveCoreAsync(action, cancellationToken),
            cancellationToken);

    private async Task<ExternalActionReservation> ReserveCoreAsync(
        ExternalActionRecord action,
        CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await context.Database.BeginTransactionAsync(
            IsolationLevel.Serializable,
            cancellationToken);
        var existing = await context.ExternalActions.SingleOrDefaultAsync(
            item => item.TenantId == action.TenantId &&
                item.IdempotencyReference == action.IdempotencyReference,
            cancellationToken);
        if (existing is null)
        {
            context.ExternalActions.Add(ToEntity(action));
            try
            {
                await context.SaveChangesAsync(cancellationToken);
                await transaction.CommitAsync(cancellationToken);
                return new ExternalActionReservation(ExternalActionReservationKind.Acquired, action);
            }
            catch (DbUpdateException exception) when (IsUniqueViolation(exception))
            {
                // A concurrent reservation won after our serializable read. PostgreSQL aborts the
                // transaction on 23505, so roll it back before re-reading the durable winner.
                // SQLite can surface the uniqueness conflict just before the winning transaction
                // becomes visible to a new reader, so use fresh contexts for a bounded visibility
                // wait. If it still is not visible, rethrow the retryable uniqueness exception and
                // let the outer contention policy retry the whole operation.
                await transaction.RollbackAsync(cancellationToken);
                for (var attempt = 1; attempt <= 5 && existing is null; attempt++)
                {
                    await using var winnerContext = await contextFactory.CreateDbContextAsync(cancellationToken);
                    existing = await winnerContext.ExternalActions.AsNoTracking().SingleOrDefaultAsync(
                        item => item.TenantId == action.TenantId &&
                            item.IdempotencyReference == action.IdempotencyReference,
                        cancellationToken);
                    if (existing is null && attempt < 5)
                    {
                        await Task.Delay(TimeSpan.FromMilliseconds(attempt * 5), cancellationToken);
                    }
                }

                if (existing is null)
                {
                    throw;
                }
            }
        }
        else
        {
            await transaction.CommitAsync(cancellationToken);
        }
        var record = ToRecord(existing);
        if (!string.Equals(record.ActionId, action.ActionId, StringComparison.Ordinal) ||
            !string.Equals(record.Capability, action.Capability, StringComparison.Ordinal) ||
            !string.Equals(record.InputReference, action.InputReference, StringComparison.Ordinal))
        {
            return new ExternalActionReservation(ExternalActionReservationKind.Conflict, record);
        }

        var kind = record.Status switch
        {
            ExternalActionStatus.Succeeded => ExternalActionReservationKind.Reused,
            ExternalActionStatus.Running => ExternalActionReservationKind.InProgress,
            ExternalActionStatus.Failed => ExternalActionReservationKind.Failed,
            _ => throw new InvalidOperationException($"Unsupported external action status '{record.Status}'.")
        };
        return new ExternalActionReservation(kind, record);
    }

    private static bool IsUniqueViolation(DbUpdateException exception) => exception.InnerException switch
    {
        Npgsql.PostgresException { SqlState: "23505" } => true,
        Microsoft.Data.Sqlite.SqliteException { SqliteExtendedErrorCode: 1555 or 2067 } => true,
        _ => false
    };

    public async Task<ExternalActionRecord> CompleteAsync(
        string tenantId,
        string idempotencyReference,
        ExternalActionResult result,
        DateTimeOffset completedAt,
        CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var entity = await GetRunningAsync(context, tenantId, idempotencyReference, cancellationToken);
        entity.Status = ExternalActionStatus.Succeeded;
        entity.ResultJson = JsonSerializer.Serialize(result, JsonOptions);
        entity.ErrorJson = null;
        entity.UpdatedAtUnixMilliseconds = completedAt.ToUnixTimeMilliseconds();
        await context.SaveChangesAsync(cancellationToken);
        return ToRecord(entity);
    }

    public async Task<ExternalActionRecord> FailAsync(
        string tenantId,
        string idempotencyReference,
        Problem problem,
        DateTimeOffset failedAt,
        CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var entity = await GetRunningAsync(context, tenantId, idempotencyReference, cancellationToken);
        entity.Status = ExternalActionStatus.Failed;
        entity.ErrorJson = JsonSerializer.Serialize(problem, JsonOptions);
        entity.UpdatedAtUnixMilliseconds = failedAt.ToUnixTimeMilliseconds();
        await context.SaveChangesAsync(cancellationToken);
        return ToRecord(entity);
    }

    private static async Task<ExternalActionEntity> GetRunningAsync(
        IRouteDbContext context,
        string tenantId,
        string idempotencyReference,
        CancellationToken cancellationToken)
    {
        var entity = await context.ExternalActions.SingleOrDefaultAsync(
            item => item.TenantId == tenantId && item.IdempotencyReference == idempotencyReference,
            cancellationToken) ?? throw new KeyNotFoundException("The external action reservation was not found.");
        if (entity.Status != ExternalActionStatus.Running)
        {
            throw new InvalidOperationException($"The external action cannot complete from {entity.Status}.");
        }

        return entity;
    }

    private static ExternalActionEntity ToEntity(ExternalActionRecord action) => new()
    {
        TenantId = action.TenantId,
        IdempotencyReference = action.IdempotencyReference,
        ExecutionId = action.ExecutionId,
        ActionId = action.ActionId,
        Capability = action.Capability,
        InputReference = action.InputReference,
        Status = action.Status,
        ResultJson = action.Result is null ? null : JsonSerializer.Serialize(action.Result, JsonOptions),
        ErrorJson = action.Error is null ? null : JsonSerializer.Serialize(action.Error, JsonOptions),
        CreatedAtUnixMilliseconds = action.CreatedAt.ToUnixTimeMilliseconds(),
        UpdatedAtUnixMilliseconds = action.UpdatedAt.ToUnixTimeMilliseconds()
    };

    public async Task<IReadOnlyList<ExternalActionRecord>> ListUnresolvedAsync(
        string tenantId,
        Guid executionId,
        CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var entities = await context.ExternalActions
            .AsNoTracking()
            .Where(item => item.TenantId == tenantId
                && item.ExecutionId == executionId
                && item.Status == ExternalActionStatus.Running)
            .OrderBy(item => item.ActionId)
            .ToArrayAsync(cancellationToken);
        return [.. entities.Select(ToRecord)];
    }

    private static ExternalActionRecord ToRecord(ExternalActionEntity entity) => new(
        entity.ExecutionId,
        entity.TenantId,
        entity.ActionId,
        entity.Capability,
        entity.IdempotencyReference,
        entity.InputReference,
        entity.Status,
        DateTimeOffset.FromUnixTimeMilliseconds(entity.CreatedAtUnixMilliseconds),
        DateTimeOffset.FromUnixTimeMilliseconds(entity.UpdatedAtUnixMilliseconds),
        entity.ResultJson is null
            ? null
            : JsonSerializer.Deserialize<ExternalActionResult>(entity.ResultJson, JsonOptions),
        entity.ErrorJson is null
            ? null
            : JsonSerializer.Deserialize<Problem>(entity.ErrorJson, JsonOptions));
}
