using iRoute.Common;

namespace iRoute.Services;

public sealed partial class ExecutionService
{
    private async Task AppendGatewayResilienceEvidenceAsync(
        Guid executionId,
        GatewayResilienceTrace trace,
        CancellationToken cancellationToken)
    {
        foreach (var candidate in trace.Candidates)
        {
            await AppendEventAsync(
                executionId,
                ExecutionEventTypes.GatewayCandidateEvaluated,
                new
                {
                    candidate.Deployment.GatewayId,
                    candidate.Deployment.Provider,
                    candidate.Deployment.DeploymentId,
                    candidate.Deployment.Region,
                    candidate.Deployment.Residency,
                    candidate.Deployment.ModelVersion,
                    candidate.Eligible,
                    candidate.Reason,
                    candidate.CircuitState,
                    candidate.FailureClass
                },
                cancellationToken);
        }

        foreach (var attempt in trace.Attempts)
        {
            var fallbackSelected = IsGatewayFallback(trace, attempt);
            _telemetry.RecordGatewayAttempt(attempt, fallbackSelected);
            await AppendEventAsync(
                executionId,
                ExecutionEventTypes.GatewayAttempted,
                new
                {
                    attempt.Deployment.GatewayId,
                    attempt.Deployment.Provider,
                    attempt.Deployment.DeploymentId,
                    attempt.Deployment.Region,
                    attempt.Deployment.Residency,
                    attempt.Deployment.ModelVersion,
                    attempt.Attempt,
                    attempt.CircuitStateBefore,
                    attempt.CircuitStateAfter,
                    attempt.Succeeded,
                    attempt.FailureClass,
                    attempt.FailureCode,
                    attempt.StatusCode,
                    attempt.DurationMilliseconds,
                    attempt.RetryAfterMilliseconds,
                    fallbackSelected
                },
                cancellationToken);
            if (fallbackSelected)
            {
                await AppendEventAsync(
                    executionId,
                    ExecutionEventTypes.GatewayFallbackSelected,
                    new
                    {
                        attempt.Deployment.GatewayId,
                        attempt.Deployment.Provider,
                        attempt.Deployment.DeploymentId,
                        attempt.Deployment.Region,
                        attempt.Deployment.ModelVersion,
                        attempt.Attempt,
                        reason = trace.FallbackReason
                    },
                    cancellationToken);
            }

            if (attempt.CircuitStateBefore != attempt.CircuitStateAfter)
            {
                await AppendEventAsync(
                    executionId,
                    ExecutionEventTypes.GatewayCircuitChanged,
                    new
                    {
                        attempt.Deployment.GatewayId,
                        attempt.Deployment.Provider,
                        attempt.Deployment.DeploymentId,
                        attempt.Deployment.Region,
                        attempt.Deployment.ModelVersion,
                        from = attempt.CircuitStateBefore,
                        to = attempt.CircuitStateAfter,
                        attempt.FailureClass
                    },
                    cancellationToken);
            }
        }

        if (trace.ExhaustionReason is not null)
        {
            await AppendEventAsync(
                executionId,
                ExecutionEventTypes.GatewayExhausted,
                new
                {
                    trace.PolicyVersion,
                    trace.ExhaustionReason,
                    candidates = trace.Candidates.Count,
                    attempts = trace.Attempts.Count
                },
                cancellationToken);
        }

        await AppendEventAsync(
            executionId,
            ExecutionEventTypes.GatewayResilienceDecided,
            new
            {
                trace.PolicyVersion,
                finalGatewayId = trace.FinalDeployment?.GatewayId,
                finalProvider = trace.FinalDeployment?.Provider,
                finalDeploymentId = trace.FinalDeployment?.DeploymentId,
                finalRegion = trace.FinalDeployment?.Region,
                finalResidency = trace.FinalDeployment?.Residency,
                finalModelVersion = trace.FinalDeployment?.ModelVersion,
                trace.FallbackReason,
                trace.ExhaustionReason,
                candidates = trace.Candidates.Count,
                attempts = trace.Attempts.Count
            },
            cancellationToken);
    }

    private static bool IsGatewayFallback(
        GatewayResilienceTrace trace,
        GatewayAttemptEvidence attempt)
    {
        if (attempt.Attempt > 1)
        {
            return true;
        }

        foreach (var candidate in trace.Candidates)
        {
            if (candidate.Eligible &&
                string.Equals(
                    candidate.Deployment.DeploymentId,
                    attempt.Deployment.DeploymentId,
                    StringComparison.Ordinal))
            {
                return false;
            }

            if (!candidate.Eligible && candidate.FailureClass is not GatewayFailureClass.Policy)
            {
                return true;
            }
        }

        return false;
    }

    private async Task AppendGatewayFailureAsync(
        Guid executionId,
        ExecutionPlanStep step,
        ModelGatewayFailure failure,
        long durationMilliseconds) =>
        await AppendEventAsync(
            executionId,
            ExecutionEventTypes.GatewayFailed,
            new
            {
                stepId = step.Id,
                step.Capability,
                step.ProfileId,
                code = failure.Code,
                kind = failure.Kind,
                retryable = failure.Retryable,
                statusCode = failure.StatusCode,
                gatewayId = failure.GatewayId,
                failureClass = failure.FailureClass,
                retryAfterMilliseconds = failure.RetryAfterMilliseconds,
                durationMilliseconds
            },
            CancellationToken.None);

    private ModelGatewayException InvalidGatewayStream(
        ModelGatewayRequest request,
        string message) =>
        new(
            ErrorCodes.ModelGatewayInvalidResponse,
            message,
            false,
            failureKind: ModelGatewayFailureKind.InvalidResponse,
            gatewayId: modelGateway.GatewayId,
            correlationId: request.CorrelationId);
}
