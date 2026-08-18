using System.Collections.Concurrent;
using iRoute.Contracts;
using iRoute.Core;

namespace iRoute.Infrastructure;

public sealed class InMemoryApprovalStore : IApprovalStore
{
    private readonly ConcurrentDictionary<(Guid ExecutionId, string ActionId), ApprovalRecord> _approvals = new();
    private readonly object _sync = new();

    public Task<ApprovalRecord> CreatePendingAsync(
        ApprovalRecord approval,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_sync)
        {
            var key = (approval.ExecutionId, approval.ActionId);
            if (_approvals.TryGetValue(key, out var existing))
            {
                EnsureSameProposal(existing, approval);
                return Task.FromResult(existing);
            }

            _approvals[key] = approval;
            return Task.FromResult(approval);
        }
    }

    public Task<ApprovalRecord?> GetAsync(
        Guid executionId,
        string actionId,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(_approvals.GetValueOrDefault((executionId, actionId)));
    }

    public Task<ApprovalDecisionResult> DecideAsync(
        Guid executionId,
        string actionId,
        bool approved,
        string actorId,
        string? reason,
        DateTimeOffset decidedAt,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_sync)
        {
            var key = (executionId, actionId);
            if (!_approvals.TryGetValue(key, out var existing))
            {
                throw new KeyNotFoundException($"Approval '{actionId}' was not found for execution '{executionId}'.");
            }

            var target = approved ? ApprovalStatus.Approved : ApprovalStatus.Denied;
            if (existing.Status != ApprovalStatus.Pending)
            {
                if (existing.Status != target)
                {
                    throw new InvalidOperationException("The approval has already been decided differently.");
                }

                return Task.FromResult(new ApprovalDecisionResult(existing, false));
            }

            var decided = existing with
            {
                Status = target,
                DecidedByActorId = actorId,
                DecidedAt = decidedAt,
                Reason = string.IsNullOrWhiteSpace(reason) ? null : reason.Trim()
            };
            _approvals[key] = decided;
            return Task.FromResult(new ApprovalDecisionResult(decided, true));
        }
    }

    private static void EnsureSameProposal(ApprovalRecord existing, ApprovalRecord proposed)
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

public sealed class InMemoryExternalActionStore : IExternalActionStore
{
    private readonly ConcurrentDictionary<(string TenantId, string Reference), ExternalActionRecord> _actions = new();
    private readonly object _sync = new();

    public Task<IReadOnlyList<ExternalActionRecord>> ListUnresolvedAsync(
        string tenantId,
        Guid executionId,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        IReadOnlyList<ExternalActionRecord> unresolved =
        [
            .. _actions.Values
                .Where(action => action.TenantId == tenantId
                    && action.ExecutionId == executionId
                    && action.Status == ExternalActionStatus.Running)
                .OrderBy(action => action.ActionId, StringComparer.Ordinal)
        ];
        return Task.FromResult(unresolved);
    }

    public Task<ExternalActionReservation> ReserveAsync(
        ExternalActionRecord action,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_sync)
        {
            var key = (action.TenantId, action.IdempotencyReference);
            if (!_actions.TryGetValue(key, out var existing))
            {
                _actions[key] = action;
                return Task.FromResult(new ExternalActionReservation(
                    ExternalActionReservationKind.Acquired,
                    action));
            }

            if (!string.Equals(existing.ActionId, action.ActionId, StringComparison.Ordinal) ||
                !string.Equals(existing.Capability, action.Capability, StringComparison.Ordinal) ||
                !string.Equals(existing.InputReference, action.InputReference, StringComparison.Ordinal))
            {
                return Task.FromResult(new ExternalActionReservation(
                    ExternalActionReservationKind.Conflict,
                    existing));
            }

            var kind = existing.Status switch
            {
                ExternalActionStatus.Succeeded => ExternalActionReservationKind.Reused,
                ExternalActionStatus.Running => ExternalActionReservationKind.InProgress,
                ExternalActionStatus.Failed => ExternalActionReservationKind.Failed,
                _ => throw new InvalidOperationException($"Unsupported external action status '{existing.Status}'.")
            };
            return Task.FromResult(new ExternalActionReservation(kind, existing));
        }
    }

    public Task<ExternalActionRecord> CompleteAsync(
        string tenantId,
        string idempotencyReference,
        ExternalActionResult result,
        DateTimeOffset completedAt,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_sync)
        {
            var key = (tenantId, idempotencyReference);
            var existing = GetRunning(key);
            var completed = existing with
            {
                Status = ExternalActionStatus.Succeeded,
                Result = result with { Output = result.Output.Clone() },
                UpdatedAt = completedAt,
                Error = null
            };
            _actions[key] = completed;
            return Task.FromResult(completed);
        }
    }

    public Task<ExternalActionRecord> FailAsync(
        string tenantId,
        string idempotencyReference,
        Problem problem,
        DateTimeOffset failedAt,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_sync)
        {
            var key = (tenantId, idempotencyReference);
            var existing = GetRunning(key);
            var failed = existing with
            {
                Status = ExternalActionStatus.Failed,
                UpdatedAt = failedAt,
                Error = problem
            };
            _actions[key] = failed;
            return Task.FromResult(failed);
        }
    }

    private ExternalActionRecord GetRunning((string TenantId, string Reference) key)
    {
        if (!_actions.TryGetValue(key, out var existing))
        {
            throw new KeyNotFoundException("The external action reservation was not found.");
        }

        if (existing.Status != ExternalActionStatus.Running)
        {
            throw new InvalidOperationException($"The external action cannot complete from {existing.Status}.");
        }

        return existing;
    }
}
