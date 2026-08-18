using System.Text.Json;
using iRoute.Common;

namespace iRoute.Services;

public sealed partial class ExecutionService
{
    private async Task<ExecutionSnapshot> RunPlanAsync(
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
            snapshot = await TransitionAsync(snapshot, ExecutionStatus.Running, cancellationToken);
        }
        var context = await contextCompiler.CompileAsync(request, definition, cancellationToken);
        await AppendEventAsync(
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
                    ExecutionStepKind.Model => await ExecuteModelStepAsync(
                        snapshot.ExecutionId,
                        request,
                        definition,
                        context,
                        step,
                        modelGatewayBudgets[step.Id],
                        dependencyOutputs,
                        stepCancellationToken),
                    ExecutionStepKind.Tool when step.SideEffectClass < SideEffectClass.ReversibleWrite =>
                        await ExecuteCapabilityStepAsync(
                            snapshot,
                            request,
                            definition,
                            step,
                            stepCancellationToken),
                    ExecutionStepKind.Tool when step.SideEffectClass >= SideEffectClass.ReversibleWrite =>
                        await ExecuteExternalActionAsync(
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
            await AppendEventAsync(
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
                    durationMilliseconds = usage.DurationMilliseconds,
                    modelCalls = usage.ModelCalls,
                    fallbackAttempts = capabilityResult.Resilience?.Attempts.Count ?? 0,
                    peakQueuedSteps = workflow.PeakQueuedSteps,
                    backpressureWaitCount = workflow.BackpressureWaitCount,
                    recoveredStepCount = workflow.RecoveredStepCount
                },
                cancellationToken);
        }

        snapshot = await TransitionAsync(snapshot, ExecutionStatus.Validating, cancellationToken);
        var validator = validators.First(x => x.Supports(request.TaskType));
        var validation = await validator.ValidateAsync(
            request,
            definition,
            capabilityResult,
            context,
            cancellationToken);
        await AppendEventAsync(
            snapshot.ExecutionId,
            ExecutionEventTypes.ValidationCompleted,
            new
            {
                validation.Passed,
                validation.Quality,
                checks = validation.Checks.Count,
                failures = validation.Failures.Count
            },
            cancellationToken);
        if (!validation.Passed)
        {
            throw new TaskExecutionException(
                ErrorCodes.ValidationFailed,
                "Task validation failed",
                string.Join(" ", validation.Failures));
        }

        snapshot = await TransitionAsync(snapshot, ExecutionStatus.Materializing, cancellationToken);
        var memory = await MaterializeProjectMemoryAsync(snapshot, request, cancellationToken);
        var combinedEvidence = capabilityResult.Evidence
            .Concat(context.Evidence)
            .DistinctBy(x => (x.Kind, x.Reference))
            .ToArray();
        var createdAt = clock.GetUtcNow();
        var dependencies = combinedEvidence
            .Select(item => new DependencyReference(item.Kind, item.Reference, item.ContentHash))
            .Concat(memory.Select(item => new DependencyReference(
                "memory",
                item.MemoryId.ToString(),
                item.ContentHash)))
            .DistinctBy(item => (item.Kind, item.Reference))
            .ToArray();
        var logicalKey = request.Metadata?.GetValueOrDefault("artifactKey")?.Trim();
        var artifact = await artifacts.SaveAsync(
            new ArtifactRecord(
                Guid.CreateVersion7(),
                snapshot.TenantId,
                request.ProjectId,
                request.TaskType,
                definition.Version,
                definition.ArtifactType,
                1,
                fingerprint.Create(request, definition.Version),
                CanonicalJson.Hash(capabilityResult.Output),
                capabilityResult.Output.Clone(),
                combinedEvidence,
                createdAt,
                definition.ArtifactTimeToLive is { } ttl ? createdAt.Add(ttl) : null,
                true,
                string.IsNullOrWhiteSpace(logicalKey) ? request.TaskType : logicalKey,
                Dependencies: dependencies),
            cancellationToken);
        await AppendEventAsync(
            snapshot.ExecutionId,
            ExecutionEventTypes.ArtifactMaterialized,
            new
            {
                artifact.ArtifactId,
                artifact.ArtifactType,
                artifact.Version,
                artifact.ContentHash,
                artifact.LogicalKey,
                artifact.LifecycleStatus,
                artifact.SupersedesArtifactId,
                dependencies = artifact.EffectiveDependencies.Count
            },
            cancellationToken);
        if (artifact.SupersedesArtifactId is not null)
        {
            await AppendEventAsync(
                snapshot.ExecutionId,
                ExecutionEventTypes.ArtifactSuperseded,
                new
                {
                    artifact.ArtifactId,
                    artifact.SupersedesArtifactId,
                    artifact.LogicalKey,
                    artifact.Version
                },
                cancellationToken);
        }

        var outcome = new TaskOutcome(
            capabilityResult.Output,
            ResolutionLevelFor(routing),
            validation.Quality,
            combinedEvidence,
            usage,
            [artifact.ToReference()],
            validation.ToContract(),
            context.Manifest,
            routing);
        return await FinishMaterializedAsync(snapshot, outcome, cancellationToken);
    }

}
