using System.Text.Json;
using iRoute.Common;
using static iRoute.Services.ExecutionSerialization;
using static iRoute.Services.ExecutionValidation;

namespace iRoute.Services;

public sealed class PlanExecutionService(
    IContextCompiler contextCompiler,
    BoundedDependencyScheduler scheduler,
    ModelStepExecutionService models,
    CapabilityStepExecutionService capabilities,
    ExternalActionExecutionService externalActions,
    ExecutionOutcomeService outcomes,
    ExecutionPersistenceService persistence)
{
    internal async Task<ExecutionSnapshot> RunPlanAsync(
        ExecutionSnapshot snapshot,
        TaskRequest request,
        TaskDefinition definition,
        ExecutionPlan plan,
        RoutingDecision routing,
        bool preserveCheckpointOnCancellation,
        CancellationToken cancellationToken)
    {
        if (snapshot.Status != ExecutionStatus.Running)
        {
            snapshot = await persistence.TransitionAsync(snapshot, ExecutionStatus.Running, cancellationToken);
        }
        var context = await contextCompiler.CompileAsync(request, definition, cancellationToken);
        await persistence.AppendEventAsync(
            snapshot.ExecutionId,
            ExecutionEventTypes.ContextCompiled,
            new
            {
                estimatedTokens = context.Manifest.EstimatedTokens,
                budgetTokens = context.Manifest.BudgetTokens,
                projectedInputTokens = context.Manifest.ProjectedInputTokens,
                contextTokens = context.Manifest.ContextTokens,
                truncated = context.Manifest.Truncated,
                fullHistoryIncluded = context.Manifest.FullHistoryIncluded,
                entries = context.Manifest.Entries.Count,
                included = context.Manifest.Entries.Count(entry => entry.Included),
                provenance = context.Manifest.Provenance.Count
            },
            cancellationToken);

        var modelGatewayBudgets = ModelGatewayBudgets(plan);
        var workflow = await scheduler.ExecuteAsync(
            snapshot.ExecutionId,
            request,
            plan,
            routing,
            async (step, dependencyOutputs, stepCancellationToken) =>
            {
                var result = step.Kind switch
                {
                    ExecutionStepKind.Model => await models.ExecuteModelStepAsync(
                        snapshot.ExecutionId,
                        request,
                        definition,
                        context,
                        step,
                        modelGatewayBudgets[step.Id],
                        dependencyOutputs,
                        stepCancellationToken),
                    ExecutionStepKind.Tool when step.SideEffectClass < SideEffectClass.ReversibleWrite =>
                        await capabilities.ExecuteCapabilityStepAsync(
                            snapshot,
                            request,
                            definition,
                            step,
                            stepCancellationToken),
                    ExecutionStepKind.Tool when step.SideEffectClass >= SideEffectClass.ReversibleWrite =>
                        await externalActions.ExecuteExternalActionAsync(
                            snapshot,
                            request,
                            step,
                            stepCancellationToken),
                    _ => throw new WorkflowStepExecutionException(
                        step.Id,
                        $"Capability '{step.Capability}' has no registered executor.")
                };
                return JsonSerializer.SerializeToElement(result, ContractJsonOptions);
            },
            cancellationToken,
            preserveCheckpointOnCancellation);
        if (!workflow.Outputs.TryGetValue("execute", out var resultOutput))
        {
            throw new WorkflowStepExecutionException(
                "execute",
                "The direct workflow completed without an 'execute' step output.");
        }

        var finalResult = resultOutput.Deserialize<ModelGatewayResult>(ContractJsonOptions)
            ?? throw new WorkflowStepExecutionException(
                "execute",
                "The checkpointed capability result is invalid.");
        var capabilityResult = AggregateWorkflowResult(plan, workflow.Outputs, finalResult);
        if (capabilityResult.Resilience is { } resilience)
        {
            routing = routing with
            {
                SelectedDeployment = resilience.FinalDeployment,
                Resilience = resilience
            };
        }
        var usage = capabilityResult.Usage;
        EnsureUsageWithinBudget(request, capabilityResult);
        if (usage.ModelCalls > 0)
        {
            await persistence.AppendEventAsync(
                snapshot.ExecutionId,
                ExecutionEventTypes.GatewayCompleted,
                new
                {
                    capability = routing.SelectedCapability,
                    profileId = routing.SelectedProfileId,
                    gatewayId = capabilityResult.GatewayId,
                    provider = capabilityResult.Deployment?.Provider,
                    deploymentId = capabilityResult.Deployment?.DeploymentId,
                    region = capabilityResult.Deployment?.Region,
                    residency = capabilityResult.Deployment?.Residency,
                    modelVersion = capabilityResult.Deployment?.ModelVersion,
                    transport = capabilityResult.Transport,
                    finishReason = capabilityResult.FinishReason,
                    inputTokens = usage.InputTokens,
                    outputTokens = usage.OutputTokens,
                    cost = usage.Cost,
                    costKnown = usage.CostKnown,
                    durationMilliseconds = usage.DurationMilliseconds,
                    modelCalls = usage.ModelCalls,
                    fallbackAttempts = capabilityResult.Resilience?.Attempts.Count ?? 0,
                    peakQueuedSteps = workflow.PeakQueuedSteps,
                    backpressureWaitCount = workflow.BackpressureWaitCount,
                    recoveredStepCount = workflow.RecoveredStepCount
                },
                cancellationToken);
        }

        return await outcomes.MaterializeAsync(snapshot, request, definition, context, routing, capabilityResult, cancellationToken);
    }
}
