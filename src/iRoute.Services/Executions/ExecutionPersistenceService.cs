using System.Text.Json;
using iRoute.Common;

namespace iRoute.Services;

public sealed partial class ExecutionService
{
    private async Task<ExecutionSnapshot> FinishAsync(
        ExecutionSnapshot snapshot,
        TaskOutcome outcome,
        CancellationToken cancellationToken)
    {
        snapshot = await TransitionAsync(snapshot, ExecutionStatus.Materializing, cancellationToken);
        return await FinishMaterializedAsync(snapshot, outcome, cancellationToken);
    }

    private async Task<ExecutionSnapshot> FinishMaterializedAsync(
        ExecutionSnapshot snapshot,
        TaskOutcome outcome,
        CancellationToken cancellationToken)
    {
        snapshot = snapshot with { Outcome = outcome, UpdatedAt = clock.GetUtcNow() };
        await store.UpdateAsync(snapshot, cancellationToken);
        snapshot = await TransitionAsync(snapshot, ExecutionStatus.Succeeded, cancellationToken);
        await AppendEventAsync(
            snapshot.ExecutionId,
            ExecutionEventTypes.Completed,
            new
            {
                snapshot.Status,
                outcome.ResolutionLevel,
                outcome.Confidence,
                artifacts = outcome.Artifacts.Count
            },
            cancellationToken);
        _telemetry.RecordTerminal(snapshot);
        return snapshot;
    }

    private async Task<ExecutionSnapshot> QueueAsync(
        ExecutionSnapshot snapshot,
        ExecutionStatus expectedStatus,
        CancellationToken cancellationToken)
    {
        var work = executionWork ?? throw new InvalidOperationException(
            "Durable execution work is not configured for asynchronous submission.");
        var queuedAt = clock.GetUtcNow();
        await work.EnqueueAsync(
            snapshot.ExecutionId,
            expectedStatus,
            queuedAt,
            cancellationToken);
        var queued = snapshot with { Status = ExecutionStatus.Queued, UpdatedAt = queuedAt };
        await AppendEventAsync(
            queued.ExecutionId,
            ExecutionEventTypes.StatusChanged,
            new { from = expectedStatus, to = ExecutionStatus.Queued },
            cancellationToken);
        await AppendEventAsync(
            queued.ExecutionId,
            ExecutionEventTypes.Queued,
            new { availableAt = queuedAt },
            cancellationToken);
        return queued;
    }

    private Task CancelCheckpointAsync(Guid executionId, CancellationToken cancellationToken) =>
        checkpoints.CancelIncompleteStepsAsync(
            executionId,
            new Problem(
                ErrorCodes.ExecutionCancelled,
                "Execution stopped",
                "The durable execution stopped before all steps completed."),
            clock.GetUtcNow(),
            cancellationToken);

    private async Task<TimeSpan> RemainingWorkerDeadlineAsync(
        Guid executionId,
        int deadlineMilliseconds,
        CancellationToken cancellationToken)
    {
        DateTimeOffset? queuedAt = null;
        await foreach (var item in store.ReadEventsAsync(executionId, 0, cancellationToken))
        {
            if (item.Type == ExecutionEventTypes.Queued)
            {
                queuedAt = item.OccurredAt;
            }
        }

        var startedAt = queuedAt ?? clock.GetUtcNow();
        return startedAt.AddMilliseconds(deadlineMilliseconds) - clock.GetUtcNow();
    }

    private async Task<ExecutionSnapshot> TerminalAsync(
        ExecutionSnapshot snapshot,
        ExecutionStatus terminal,
        Problem problem,
        CancellationToken cancellationToken)
    {
        var latest = await store.GetAsync(snapshot.ExecutionId, cancellationToken) ?? snapshot;
        if (IsTerminal(latest.Status))
        {
            return latest;
        }

        ExecutionStateMachine.EnsureCanTransition(latest.Status, terminal);
        var updated = latest with { Status = terminal, UpdatedAt = clock.GetUtcNow(), Error = problem };
        await store.UpdateAsync(updated, cancellationToken);
        await AppendEventAsync(
            updated.ExecutionId,
            ExecutionEventTypes.StatusChanged,
            new { from = latest.Status, to = terminal },
            cancellationToken);
        await AppendEventAsync(
            updated.ExecutionId,
            ExecutionEventTypes.Failed,
            new { status = terminal, problem.Code, problem.Title, problem.Retryable },
            cancellationToken);
        _telemetry.RecordTerminal(updated);
        return updated;
    }

    private async Task<ExecutionSnapshot> TransitionAsync(
        ExecutionSnapshot snapshot,
        ExecutionStatus target,
        CancellationToken cancellationToken)
    {
        ExecutionStateMachine.EnsureCanTransition(snapshot.Status, target);
        var updated = snapshot with { Status = target, UpdatedAt = clock.GetUtcNow() };
        await store.UpdateAsync(updated, cancellationToken);
        await AppendEventAsync(
            updated.ExecutionId,
            ExecutionEventTypes.StatusChanged,
            new { from = snapshot.Status, to = target },
            cancellationToken);
        return updated;
    }

    private async Task<ExecutionEvent> AppendEventAsync(
        Guid executionId,
        string eventType,
        object data,
        CancellationToken cancellationToken)
    {
        var executionEvent = await store.AppendEventAsync(
            executionId,
            eventType,
            clock.GetUtcNow(),
            JsonSerializer.SerializeToElement(data),
            cancellationToken);
        _telemetry.RecordEvent(eventType);
        return executionEvent;
    }

    private Task<ExecutionEvent> AppendResolutionDecisionAsync(
        Guid executionId,
        string resolver,
        ResolutionDecision decision,
        CancellationToken cancellationToken) =>
        AppendEventAsync(
            executionId,
            ExecutionEventTypes.ResolutionConsidered,
            new
            {
                resolver,
                accepted = decision.Accepted,
                code = decision.Code,
                reason = decision.Reason,
                permissionChecked = decision.PermissionChecked,
                freshnessChecked = decision.FreshnessChecked,
                checks = decision.Checks.Count,
                level = decision.Candidate?.Level.ToString()
            },
            cancellationToken);

    private async Task AppendRoutingEventsAsync(
        Guid executionId,
        RoutingDecision decision,
        CancellationToken cancellationToken)
    {
        var data = new
        {
            policyVersion = decision.PolicyVersion,
            path = decision.Path,
            decision.Reason,
            selectedCapability = decision.SelectedCapability,
            selectedProfileId = decision.SelectedProfileId,
            selectedModelTier = decision.SelectedModelTier,
            qualityFloor = decision.QualityFloor,
            expectedQuality = decision.ExpectedQuality,
            expectedCost = decision.ExpectedCost,
            expectedLatencyMilliseconds = decision.ExpectedLatencyMilliseconds,
            uncertainty = decision.Uncertainty,
            score = decision.Score,
            plannerInvoked = decision.PlannerInvoked,
            planningCalls = decision.PlanningCalls,
            escalated = decision.Escalated,
            escalationReason = decision.EscalationReason,
            candidates = decision.Candidates
        };
        await AppendEventAsync(
            executionId,
            ExecutionEventTypes.RoutingDecided,
            data,
            cancellationToken);
        if (decision.Escalated)
        {
            await AppendEventAsync(
                executionId,
                ExecutionEventTypes.RoutingEscalated,
                data,
                cancellationToken);
        }
    }

}
