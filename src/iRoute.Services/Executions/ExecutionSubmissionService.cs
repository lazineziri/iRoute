using iRoute.Common;
using static iRoute.Services.ExecutionValidation;

namespace iRoute.Services;

public sealed class ExecutionSubmissionService(
    IExecutionStore store,
    IInputFingerprint fingerprint,
    IExecutionCancellationRegistry cancellations,
    TimeProvider clock,
    IExecutionTelemetry telemetry,
    ExecutionPreparationService preparation,
    PlanExecutionService plans,
    ExecutionPersistenceService persistence)
{
    public Task<ExecutionSnapshot> ExecuteAsync(TaskRequest request, CancellationToken cancellationToken) =>
        ExecuteCoreAsync(request, false, cancellationToken);

    public Task<ExecutionSnapshot> SubmitAsync(TaskRequest request, CancellationToken cancellationToken) =>
        ExecuteCoreAsync(request, true, cancellationToken);

    internal async Task<ExecutionSnapshot> ExecuteCoreAsync(
        TaskRequest request,
        bool deferExecution,
        CancellationToken cancellationToken)
    {
        Validate(request);
        var tenantId = RequestScope.Tenant(request);
        var actorId = RequestScope.Actor(request);

        var submissionFingerprint = string.IsNullOrWhiteSpace(request.IdempotencyKey)
            ? null
            : fingerprint.CreateForSubmission(request);

        if (!string.IsNullOrWhiteSpace(request.IdempotencyKey))
        {
            var existing = await store.FindByIdempotencyKeyAsync(
                tenantId,
                request.IdempotencyKey,
                cancellationToken);
            if (existing is not null)
            {
                return ReplayOrConflict(existing, request.IdempotencyKey, submissionFingerprint);
            }
        }

        var now = clock.GetUtcNow();
        var snapshot = new ExecutionSnapshot(
            Guid.CreateVersion7(),
            request.TaskType,
            ExecutionStatus.Accepted,
            now,
            now,
            TenantId: tenantId,
            ActorId: actorId,
            ProjectId: request.ProjectId);
        using var trace = telemetry.StartExecution(
            snapshot,
            request.PermissionScopes ?? [],
            "execute");
        try
        {
            await store.CreateAsync(
                snapshot,
                request.IdempotencyKey,
                submissionFingerprint,
                cancellationToken);
        }
        catch (IdempotencyConflictException) when (!string.IsNullOrWhiteSpace(request.IdempotencyKey))
        {
            // A concurrent submit carrying the same key won the insert. Answer with the execution
            // that was actually created rather than failing the retry this key exists to support.
            var winner = await store.FindByIdempotencyKeyAsync(
                tenantId,
                request.IdempotencyKey,
                cancellationToken)
                ?? throw new IdempotencyKeyReusedException(request.IdempotencyKey);

            return ReplayOrConflict(winner, request.IdempotencyKey, submissionFingerprint);
        }
        var registeredCancellation = default(CancellationToken);
        CancellationTokenSource? deadlineSource = null;
        CancellationTokenSource? executionSource = null;
        var cancellationRegistered = false;
        try
        {
            await persistence.AppendEventAsync(
                snapshot.ExecutionId,
                ExecutionEventTypes.Created,
                new
                {
                    snapshot.TaskType,
                    snapshot.TenantId,
                    snapshot.ActorId,
                    snapshot.ProjectId,
                    traceId = trace.TraceId
                },
                cancellationToken);

            registeredCancellation = cancellations.Register(snapshot.ExecutionId, cancellationToken);
            cancellationRegistered = true;
            var requestedDeadline = request.Constraints?.DeadlineMilliseconds ?? 30000;
            deadlineSource = new CancellationTokenSource(TimeSpan.FromMilliseconds(requestedDeadline), clock);
            executionSource = CancellationTokenSource.CreateLinkedTokenSource(
                registeredCancellation,
                deadlineSource.Token);
            var executionToken = executionSource.Token;

            var prepared = await preparation.PrepareAsync(snapshot, request, executionToken);
            snapshot = prepared.Snapshot;
            if (prepared.Plan is null || prepared.Routing is null) return snapshot;
            if (deferExecution) return await persistence.QueueAsync(snapshot, ExecutionStatus.Planning, executionToken);
            return await plans.RunPlanAsync(snapshot, request, prepared.Definition, prepared.Plan, prepared.Routing, false, executionToken);
        }
        catch (OperationCanceledException)
        {
            var timedOut = deadlineSource?.IsCancellationRequested is true &&
                !registeredCancellation.IsCancellationRequested;
            return await persistence.TerminalAsync(
                snapshot,
                timedOut ? ExecutionStatus.TimedOut : ExecutionStatus.Cancelled,
                timedOut
                    ? new Problem(ErrorCodes.ExecutionTimedOut, "Execution timed out", "The execution exceeded its deadline.", true)
                    : new Problem(ErrorCodes.ExecutionCancelled, "Execution cancelled", "The execution was cancelled."),
                CancellationToken.None);
        }
        catch (Exception exception)
        {
            return await persistence.HandleResumedFailureAsync(snapshot, exception, false);
        }
        finally
        {
            executionSource?.Dispose();
            deadlineSource?.Dispose();
            if (cancellationRegistered)
            {
                cancellations.Complete(snapshot.ExecutionId);
            }
        }
    }
}
