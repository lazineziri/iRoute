using iRoute.Common;
using static iRoute.Services.ExecutionValidation;

namespace iRoute.Services;

public sealed class ExecutionOutcomeService(
    IArtifactStore artifacts,
    ProjectStateExecutionService projectState,
    IInputFingerprint fingerprint,
    TimeProvider clock,
    ExecutionPersistenceService persistence,
    IEnumerable<ITaskOutcomeValidator> validators)
{
    internal async Task<ExecutionSnapshot> MaterializeAsync(
        ExecutionSnapshot snapshot, TaskRequest request, TaskDefinition definition, CompiledContext context,
        RoutingDecision routing, ModelGatewayResult capabilityResult, CancellationToken cancellationToken)
    {
        var usage = capabilityResult.Usage;
        snapshot = await persistence.TransitionAsync(snapshot, ExecutionStatus.Validating, cancellationToken);
        var validator = validators.First(x => x.Supports(request.TaskType));
        var validation = await validator.ValidateAsync(
            request,
            definition,
            capabilityResult,
            context,
            cancellationToken);
        await persistence.AppendEventAsync(
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

        snapshot = await persistence.TransitionAsync(snapshot, ExecutionStatus.Materializing, cancellationToken);
        var memory = await projectState.MaterializeProjectMemoryAsync(snapshot, request, cancellationToken);
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
                Dependencies: dependencies,
                Confidence: validation.Quality),
            cancellationToken);
        await persistence.AppendEventAsync(
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
            await persistence.AppendEventAsync(
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
        return await persistence.FinishMaterializedAsync(snapshot, outcome, cancellationToken);
    }
}
