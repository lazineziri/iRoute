using System.Diagnostics;
using iRoute.Common;

namespace iRoute.Services;

public sealed partial class ResilientModelGateway(
    IGatewayDeploymentRegistry registry,
    IGatewayDeploymentClientFactory clients,
    IGatewayCircuitStore circuits,
    TimeProvider clock,
    GatewayResilienceOptions options) : IModelGateway
{
    public const string PolicyVersion = "gateway-resilience.w18.v1";
    private readonly string _ownerId = $"{Environment.MachineName}:{Environment.ProcessId}:{Guid.CreateVersion7():N}";

    public string GatewayId => "iroute-resilience-router";

    public async Task<ModelGatewayResult> ExecuteAsync(
        ModelGatewayRequest request,
        CancellationToken cancellationToken)
    {
        options.EnsureValid();
        var started = Stopwatch.StartNew();
        var candidates = new List<GatewayCandidateEvidence>();
        var attempts = new List<GatewayAttemptEvidence>();
        var deployments = (await registry.ListAsync(cancellationToken))
            .OrderBy(item => item.Priority)
            .ThenBy(item => item.EstimatedCost)
            .ThenBy(item => item.ExpectedLatencyMilliseconds)
            .ThenByDescending(item => item.ExpectedQuality)
            .ThenBy(item => item.DeploymentId, StringComparer.Ordinal)
            .ToArray();
        var circuitStates = (await circuits.ListAsync(cancellationToken))
            .ToDictionary(item => item.DeploymentId, StringComparer.Ordinal);
        var eligible = new List<GatewayDeployment>();
        foreach (var deployment in deployments)
        {
            var circuit = circuitStates.GetValueOrDefault(deployment.DeploymentId) ??
                InitialCircuit(deployment.DeploymentId, clock.GetUtcNow());
            var rejection = StaticRejection(deployment, request);
            if (rejection is null)
            {
                eligible.Add(deployment);
            }
            else
            {
                candidates.Add(new GatewayCandidateEvidence(
                    deployment.ToReference(),
                    false,
                    rejection,
                    circuit.State,
                    GatewayFailureClass.Policy));
            }
        }

        var maximumAttempts = Math.Min(
            Math.Max(1, request.MaximumAttempts ?? options.MaximumAttempts),
            options.MaximumAttempts);
        var estimatedCost = 0m;
        string? fallbackReason = null;
        string? terminalReason = null;
        GatewayFailureClass? finalFailureClass = null;
        TimeSpan? finalRetryAfter = null;
        var fallbackSuppressed = false;
        for (var deploymentIndex = 0; deploymentIndex < eligible.Count; deploymentIndex++)
        {
            var deployment = eligible[deploymentIndex];
            if (fallbackSuppressed)
            {
                candidates.Add(Rejected(
                    deployment,
                    circuitStates,
                    "A permanent failure made further deployment attempts unsafe."));
                continue;
            }

            if (attempts.Count >= maximumAttempts)
            {
                candidates.Add(Rejected(
                    deployment,
                    circuitStates,
                    "The model-call attempt budget is exhausted."));
                continue;
            }

            if (request.MaximumCost is { } maximumCost &&
                estimatedCost + deployment.EstimatedCost > maximumCost)
            {
                candidates.Add(Rejected(
                    deployment,
                    circuitStates,
                    $"The fallback would exceed the {maximumCost:0.####} cost ceiling."));
                continue;
            }

            var remainingMilliseconds = RemainingMilliseconds(request, started);
            if (remainingMilliseconds <= 0 ||
                deployment.ExpectedLatencyMilliseconds > remainingMilliseconds)
            {
                candidates.Add(Rejected(
                    deployment,
                    circuitStates,
                    "The fallback cannot complete inside the remaining deadline."));
                continue;
            }

            var remainingAttemptSlots = Math.Min(
                maximumAttempts - attempts.Count,
                eligible.Count - deploymentIndex);
            var attemptDeadlineMilliseconds = Math.Max(
                1,
                remainingMilliseconds / Math.Max(1, remainingAttemptSlots));

            var permit = await circuits.TryAcquireAsync(
                deployment.DeploymentId,
                _ownerId,
                options.Circuit,
                clock.GetUtcNow(),
                cancellationToken);
            circuitStates[deployment.DeploymentId] = permit.Snapshot;
            if (!permit.Granted)
            {
                candidates.Add(new GatewayCandidateEvidence(
                    deployment.ToReference(),
                    false,
                    permit.Reason,
                    permit.State,
                    permit.Snapshot.LastFailureClass ?? GatewayFailureClass.Provider));
                fallbackReason ??=
                    $"Fallback from deployment '{deployment.DeploymentId}' because {permit.Reason}";
                continue;
            }

            candidates.Add(new GatewayCandidateEvidence(
                deployment.ToReference(),
                true,
                permit.Reason,
                permit.State));
            var effectiveAttemptDeadlineMilliseconds = permit.State == GatewayCircuitState.HalfOpen
                ? Math.Min(
                    attemptDeadlineMilliseconds,
                    Math.Max(
                        1,
                        checked((int)Math.Min(
                            int.MaxValue,
                            options.Circuit.EffectiveProbeLeaseDuration.TotalMilliseconds / 2))))
                : attemptDeadlineMilliseconds;
            estimatedCost += deployment.EstimatedCost;
            var attemptNumber = attempts.Count + 1;
            var attemptTimer = Stopwatch.StartNew();
            try
            {
                using var attemptDeadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                attemptDeadline.CancelAfter(TimeSpan.FromMilliseconds(effectiveAttemptDeadlineMilliseconds));
                var result = await clients.GetClient(deployment).ExecuteAsync(
                    request with
                    {
                        DeadlineMilliseconds = effectiveAttemptDeadlineMilliseconds,
                        MaximumAttempts = 1
                    },
                    attemptDeadline.Token);
                EnsureUsableResult(request, deployment, result);
                attemptTimer.Stop();
                var after = await circuits.RecordSuccessAsync(
                    permit,
                    clock.GetUtcNow(),
                    cancellationToken);
                circuitStates[deployment.DeploymentId] = after;
                attempts.Add(new GatewayAttemptEvidence(
                    deployment.ToReference(),
                    attemptNumber,
                    permit.State,
                    after.State,
                    true,
                    null,
                    null,
                    null,
                    attemptTimer.ElapsedMilliseconds));
                var trace = new GatewayResilienceTrace(
                    PolicyVersion,
                    candidates.ToArray(),
                    attempts.ToArray(),
                    deployment.ToReference(),
                    fallbackReason,
                    null);
                return result with
                {
                    GatewayId = deployment.GatewayId,
                    Deployment = deployment.ToReference(),
                    Resilience = trace,
                    Usage = result.Usage with
                    {
                        ModelCalls = attempts.Count - 1 + Math.Max(1, result.Usage.ModelCalls),
                        Cost = estimatedCost - deployment.EstimatedCost +
                            Math.Max(deployment.EstimatedCost, result.Usage.Cost),
                        DurationMilliseconds = started.ElapsedMilliseconds
                    }
                };
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                attemptTimer.Stop();
                var failure = new ModelGatewayException(
                    ErrorCodes.ModelGatewayUnavailable,
                    "The generic gateway deployment exceeded its bounded share of the remaining deadline.",
                    true,
                    failureKind: ModelGatewayFailureKind.Timeout,
                    gatewayId: deployment.GatewayId,
                    correlationId: request.CorrelationId,
                    failureClass: GatewayFailureClass.Timeout);
                var observation = await RecordFailureAsync(
                    deployment,
                    permit,
                    attemptNumber,
                    attemptTimer.ElapsedMilliseconds,
                    failure,
                    cancellationToken);
                attempts.Add(observation.Attempt);
                circuitStates[deployment.DeploymentId] = observation.Circuit;
                finalFailureClass = observation.Attempt.FailureClass;
                fallbackReason ??= FallbackReason(deployment, observation.Attempt);
            }
            catch (ModelGatewayException exception)
            {
                attemptTimer.Stop();
                var observation = await RecordFailureAsync(
                    deployment,
                    permit,
                    attemptNumber,
                    attemptTimer.ElapsedMilliseconds,
                    exception,
                    cancellationToken);
                attempts.Add(observation.Attempt);
                circuitStates[deployment.DeploymentId] = observation.Circuit;
                finalFailureClass = observation.Attempt.FailureClass;
                finalRetryAfter = exception.RetryAfter;
                if (observation.Attempt.FailureClass == GatewayFailureClass.Permanent)
                {
                    fallbackSuppressed = true;
                    terminalReason =
                        $"Deployment '{deployment.DeploymentId}' returned a permanent failure; further fallback was suppressed.";
                }
                else
                {
                    fallbackReason ??= FallbackReason(deployment, observation.Attempt);
                }
            }
            catch (Exception exception)
            {
                attemptTimer.Stop();
                var failure = new ModelGatewayException(
                    ErrorCodes.ModelGatewayUnavailable,
                    "The generic gateway transport failed.",
                    true,
                    innerException: exception,
                    failureKind: ModelGatewayFailureKind.Unavailable,
                    gatewayId: deployment.GatewayId,
                    correlationId: request.CorrelationId,
                    failureClass: GatewayFailureClass.Transport);
                var observation = await RecordFailureAsync(
                    deployment,
                    permit,
                    attemptNumber,
                    attemptTimer.ElapsedMilliseconds,
                    failure,
                    cancellationToken);
                attempts.Add(observation.Attempt);
                circuitStates[deployment.DeploymentId] = observation.Circuit;
                finalFailureClass = observation.Attempt.FailureClass;
                fallbackReason ??= FallbackReason(deployment, observation.Attempt);
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        var exhaustion = terminalReason ?? (attempts.Count == 0
            ? "No registered generic gateway deployment satisfied policy and circuit eligibility."
            : $"All {attempts.Count} bounded gateway attempt(s) failed or exhausted the task budget.");
        var exhaustedTrace = new GatewayResilienceTrace(
            PolicyVersion,
            candidates.ToArray(),
            attempts.ToArray(),
            null,
            fallbackReason,
            exhaustion);
        throw new ModelGatewayException(
            ErrorCodes.ModelGatewayExhausted,
            exhaustion,
            false,
            failureKind: ModelGatewayFailureKind.Unavailable,
            gatewayId: GatewayId,
            correlationId: request.CorrelationId,
            retryAfter: finalRetryAfter,
            failureClass: finalFailureClass ?? GatewayFailureClass.Policy,
            resilience: exhaustedTrace);
    }

}
