using iRoute.Common;
using static iRoute.Services.ExecutionValidation;

namespace iRoute.Services;

public sealed class ExecutionPreparationService(
    IExecutionStore store,
    ITaskDefinitionRegistry taskDefinitions,
    ITaskRouter taskRouter,
    IExecutionPlanValidator planValidator,
    ITaskPolicyEngine policyEngine,
    IWorkflowCheckpointStore checkpoints,
    ApprovalRequestService approvalRequests,
    ExecutionResolutionService resolution,
    ExecutionPersistenceService persistence,
    TimeProvider clock)
{
    internal async Task<PreparedExecution> PrepareAsync(ExecutionSnapshot snapshot, TaskRequest request, CancellationToken cancellationToken)
    {
        var definition = await taskDefinitions.FindAsync(request.TaskType, cancellationToken)
            ?? throw new TaskExecutionException(
                ErrorCodes.UnknownTaskType,
                "Unknown task type",
                $"No active task definition exists for '{request.TaskType}'.");
        snapshot = snapshot with { TaskDefinitionVersion = definition.Version, UpdatedAt = clock.GetUtcNow() };
        await store.UpdateAsync(snapshot, cancellationToken);

        snapshot = await persistence.TransitionAsync(snapshot, ExecutionStatus.Resolving, cancellationToken);
        var resolved = await resolution.TryResolveAsync(snapshot, request, definition, cancellationToken);
        if (resolved is not null) return new PreparedExecution(resolved, definition);

        snapshot = await persistence.TransitionAsync(snapshot, ExecutionStatus.Planning, cancellationToken);
        var routing = await taskRouter.RouteAsync(request, definition, cancellationToken);
        var plan = routing.Plan;
        EnsureModelBudgetAllows(request, plan);
        planValidator.EnsureValid(plan);
        await persistence.AppendRoutingEventsAsync(snapshot.ExecutionId, routing.Decision, cancellationToken);
        await persistence.AppendEventAsync(
            snapshot.ExecutionId,
            ExecutionEventTypes.PlanValidated,
            new
            {
                plan.PlanId,
                plan.Version,
                steps = plan.Steps.Count,
                plan.Budget.MaxModelCalls,
                plan.Budget.MaxToolCalls,
                plan.Budget.MaxTaskDepth,
                plan.Budget.DeadlineMilliseconds
            },
            cancellationToken);
        var initialization = await checkpoints.InitializeAsync(
            snapshot.ExecutionId,
            request,
            plan,
            routing.Decision,
            clock.GetUtcNow(),
            cancellationToken);
        if (initialization.Created)
        {
            await persistence.AppendEventAsync(
                snapshot.ExecutionId,
                ExecutionEventTypes.WorkflowCheckpointed,
                new { plan.PlanId, steps = plan.Steps.Count },
                cancellationToken);
        }

        var policy = policyEngine.Evaluate(request, definition, plan);
        await persistence.AppendPolicyEventAsync(snapshot, policy, cancellationToken);
        if (policy.Decision == PolicyDecisionKind.Denied)
        {
            throw new TaskExecutionException(
                policy.Code ?? ErrorCodes.ExecutionFailed,
                "Task policy denied execution",
                policy.Reason ?? "The task policy denied execution.");
        }

        EnsurePlanMatchesDefinition(plan, definition);
        if (policy.Decision == PolicyDecisionKind.ApprovalRequired)
        {
            var approval = await approvalRequests.CreateApprovalAsync(
                snapshot,
                request,
                plan,
                policy,
                cancellationToken);
            snapshot = await persistence.TransitionAsync(snapshot, ExecutionStatus.WaitingForApproval, cancellationToken);
            await persistence.AppendEventAsync(
                snapshot.ExecutionId,
                ExecutionEventTypes.ApprovalRequired,
                new
                {
                    actionId = approval.ActionId,
                    capability = approval.Capability,
                    sideEffectClass = approval.SideEffectClass,
                    requiredPermissionScopes = approval.RequiredPermissionScopes,
                    requestedByActorId = approval.RequestedByActorId,
                    inputReference = approval.InputReference,
                    idempotencyReference = approval.IdempotencyReference,
                    policyVersion = policy.PolicyVersion
                },
                cancellationToken);
            return new PreparedExecution(snapshot, definition);
        }

        return new PreparedExecution(snapshot, definition, plan, routing.Decision);

    }
}
