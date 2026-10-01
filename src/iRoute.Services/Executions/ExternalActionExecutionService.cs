using System.Diagnostics;
using iRoute.Common;

namespace iRoute.Services;

public sealed class ExternalActionExecutionService(
    IExternalActionStore externalActions,
    IExternalActionExecutor externalActionExecutor,
    TimeProvider clock,
    ExecutionPersistenceService persistence)
{
    internal async Task<ModelGatewayResult> ExecuteExternalActionAsync(
        ExecutionSnapshot snapshot,
        TaskRequest request,
        ExecutionPlanStep step,
        CancellationToken cancellationToken)
    {
        var inputReference = CanonicalJson.Hash(request.Input);
        var idempotencyReference = PolicyReferences.CreateActionIdempotencyReference(
            snapshot.TenantId,
            request.IdempotencyKey!,
            step.Id,
            step.Capability);
        var now = clock.GetUtcNow();
        var reservation = await externalActions.ReserveAsync(
            new ExternalActionRecord(
                snapshot.ExecutionId,
                snapshot.TenantId,
                step.Id,
                step.Capability,
                idempotencyReference,
                inputReference,
                ExternalActionStatus.Running,
                now,
                now),
            cancellationToken);
        if (reservation.Kind == ExternalActionReservationKind.Reused)
        {
            var reused = reservation.Action.Result
                ?? throw new ExternalActionExecutionException(
                    ErrorCodes.ExternalActionFailed,
                    "External action result missing",
                    "The completed external action has no durable result.");
            await persistence.AppendEventAsync(
                snapshot.ExecutionId,
                ExecutionEventTypes.ExternalActionReused,
                new
                {
                    actionId = step.Id,
                    capability = step.Capability,
                    inputReference,
                    idempotencyReference,
                    resultReference = CanonicalJson.Hash(reused.Output)
                },
                cancellationToken);
            return new ModelGatewayResult(
                reused.Output,
                new UsageSummary(ToolCalls: 1),
                1m,
                reused.Evidence);
        }

        if (reservation.Kind != ExternalActionReservationKind.Acquired)
        {
            var (code, title, detail, retryable) = reservation.Kind switch
            {
                ExternalActionReservationKind.Conflict => (
                    ErrorCodes.ExternalActionIdempotencyConflict,
                    "External action idempotency conflict",
                    "The idempotency reference is already bound to a different action or input.",
                    false),
                ExternalActionReservationKind.InProgress => (
                    ErrorCodes.ExternalActionInProgress,
                    "External action already in progress",
                    "A previous attempt reserved this action; reconciliation is required before retrying.",
                    true),
                _ => (
                    ErrorCodes.ExternalActionFailed,
                    "External action previously failed",
                    "The idempotent external action is in a failed state and was not executed again.",
                    false)
            };
            throw new ExternalActionExecutionException(code, title, detail, retryable);
        }

        await persistence.AppendEventAsync(
            snapshot.ExecutionId,
            ExecutionEventTypes.ExternalActionStarted,
            new
            {
                actionId = step.Id,
                capability = step.Capability,
                sideEffectClass = step.SideEffectClass,
                actorId = snapshot.ActorId,
                inputReference,
                idempotencyReference
            },
            cancellationToken);
        try
        {
            var stopwatch = Stopwatch.StartNew();
            var result = await externalActionExecutor.ExecuteAsync(
                new ExternalActionRequest(
                    snapshot.ExecutionId,
                    step.Id,
                    step.Capability,
                    request.Input,
                    idempotencyReference,
                    snapshot.TenantId,
                    snapshot.ActorId,
                    request.ProjectId,
                    request.PermissionScopes,
                    TaskPolicyEngine.CurrentPolicyVersion,
                    step.SideEffectClass,
                    step.TimeoutMilliseconds),
                cancellationToken);
            stopwatch.Stop();
            // The side effect has returned success. Persist that fact even if cancellation raced
            // with the response; treating a known success as indeterminate would invite a repeat.
            await externalActions.CompleteAsync(
                snapshot.TenantId,
                idempotencyReference,
                result,
                clock.GetUtcNow(),
                CancellationToken.None);
            await persistence.AppendEventAsync(
                snapshot.ExecutionId,
                ExecutionEventTypes.ExternalActionCompleted,
                new
                {
                    actionId = step.Id,
                    capability = step.Capability,
                    inputReference,
                    idempotencyReference,
                    resultReference = CanonicalJson.Hash(result.Output),
                    durationMilliseconds = stopwatch.ElapsedMilliseconds
                },
                CancellationToken.None);
            return new ModelGatewayResult(
                result.Output,
                new UsageSummary(DurationMilliseconds: stopwatch.ElapsedMilliseconds, ToolCalls: 1),
                1m,
                result.Evidence);
        }
        catch (OperationCanceledException)
        {
            await persistence.AppendEventAsync(
                snapshot.ExecutionId,
                ExecutionEventTypes.ExternalActionFailed,
                new
                {
                    actionId = step.Id,
                    capability = step.Capability,
                    inputReference,
                    idempotencyReference,
                    status = "indeterminate",
                    retryable = false
                },
                CancellationToken.None);
            throw;
        }
        catch (Exception exception)
        {
            var problem = new Problem(
                ErrorCodes.ExternalActionFailed,
                "External action failed",
                exception.Message);
            await externalActions.FailAsync(
                snapshot.TenantId,
                idempotencyReference,
                problem,
                clock.GetUtcNow(),
                CancellationToken.None);
            await persistence.AppendEventAsync(
                snapshot.ExecutionId,
                ExecutionEventTypes.ExternalActionFailed,
                new
                {
                    actionId = step.Id,
                    capability = step.Capability,
                    inputReference,
                    idempotencyReference,
                    status = "failed",
                    retryable = false
                },
                CancellationToken.None);
            throw new ExternalActionExecutionException(
                problem.Code,
                problem.Title,
                problem.Detail,
                innerException: exception);
        }
    }
}
