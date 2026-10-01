using iRoute.Common;
using static iRoute.Services.ExecutionValidation;

namespace iRoute.Services;

public sealed class ExecutionResolutionService(
    IEnumerable<INoModelResolver> resolvers,
    IEnumerable<ITaskOutcomeValidator> validators,
    ExecutionPersistenceService persistence)
{
    internal async Task<ExecutionSnapshot?> TryResolveAsync(ExecutionSnapshot snapshot, TaskRequest request, TaskDefinition definition, CancellationToken cancellationToken)
    {
        if (definition.SideEffectClass >= SideEffectClass.ReversibleWrite)
        {
            foreach (var resolver in resolvers.OrderBy(item => item.Order))
            {
                await persistence.AppendResolutionDecisionAsync(
                    snapshot.ExecutionId,
                    resolver.Name,
                    new ResolutionDecision(
                        false,
                        ResolutionDecisionCodes.ExternalWriteBlocked,
                        "External-write outcomes cannot bypass current permission and approval policy.",
                        false,
                        false,
                        ["The task side-effect class was checked before state lookup."]),
                    cancellationToken);
            }
        }
        else
        {
            foreach (var resolver in resolvers.OrderBy(x => x.Order))
            {
                var decision = await resolver.ResolveAsync(request, definition, cancellationToken);
                var candidate = decision.Candidate;
                OutcomeValidationResult? validation = null;
                if (decision.Accepted && candidate is not null)
                {
                    var validator = validators.First(item => item.Supports(request.TaskType));
                    validation = await validator.ValidateAsync(
                        request,
                        definition,
                        new ModelGatewayResult(
                            candidate.Output,
                            candidate.Usage ?? new UsageSummary(),
                            candidate.Confidence,
                            candidate.Evidence),
                        EmptyCompiledContext(),
                        cancellationToken);
                    if (!validation.Passed)
                    {
                        decision = decision with
                        {
                            Accepted = false,
                            Code = ResolutionDecisionCodes.ValidationFailed,
                            Reason = $"The reusable result failed task validation: {string.Join(" ", validation.Failures)}",
                            Checks = decision.Checks.Concat(validation.Checks).ToArray(),
                            Candidate = null
                        };
                        candidate = null;
                    }
                }

                await persistence.AppendResolutionDecisionAsync(
                    snapshot.ExecutionId,
                    resolver.Name,
                    decision,
                    cancellationToken);
                if (!decision.Accepted || candidate is null || validation is null)
                {
                    continue;
                }

                snapshot = await persistence.TransitionAsync(snapshot, ExecutionStatus.Validating, cancellationToken);
                await persistence.AppendEventAsync(
                    snapshot.ExecutionId,
                    ExecutionEventTypes.ValidationCompleted,
                    new
                    {
                        validation.Passed,
                        validation.Quality,
                        checks = validation.Checks.Count,
                        failures = validation.Failures.Count,
                        source = resolver.Name
                    },
                    cancellationToken);
                var reusedValidation = new ValidationSummary(
                    true,
                    validation.Quality,
                    decision.Checks.Concat(validation.Checks).Distinct(StringComparer.Ordinal).ToArray(),
                    []);
                var reusedOutcome = new TaskOutcome(
                    candidate.Output,
                    candidate.Level,
                    validation.Quality,
                    candidate.Evidence,
                    candidate.Usage ?? new UsageSummary(),
                    candidate.Artifact is null ? [] : [candidate.Artifact],
                    reusedValidation,
                    new ContextManifest(
                        0,
                        0,
                        0,
                        0,
                        false,
                        false,
                        [],
                        new Dictionary<string, EvidenceReference>(StringComparer.Ordinal)));
                return await persistence.FinishAsync(snapshot, reusedOutcome, cancellationToken);
            }
        }


        return null;
    }
}
