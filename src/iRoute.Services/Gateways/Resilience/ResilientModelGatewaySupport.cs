using System.Diagnostics;
using System.Runtime.CompilerServices;
using iRoute.Common;

namespace iRoute.Services;

public sealed partial class ResilientModelGateway
{
    private static GatewayCircuitSnapshot InitialCircuit(string deploymentId, DateTimeOffset now) => new(
        deploymentId,
        GatewayCircuitState.Closed,
        0,
        0,
        null,
        null,
        null,
        null,
        null,
        null,
        null,
        now);

    public async IAsyncEnumerable<ModelGatewayStreamEvent> StreamAsync(
        ModelGatewayRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var result = await ExecuteAsync(request, cancellationToken);
        yield return new ModelGatewayStreamEvent(1, ModelGatewayStreamEventKind.Completed, Result: result);
    }

    public async Task<ModelGatewayHealth> CheckHealthAsync(CancellationToken cancellationToken)
    {
        var deployments = await registry.ListAsync(cancellationToken);
        var states = (await circuits.ListAsync(cancellationToken))
            .ToDictionary(item => item.DeploymentId, StringComparer.Ordinal);
        var enabled = deployments.Where(item => item.Enabled).ToArray();
        var available = enabled.Count(item =>
            states.GetValueOrDefault(item.DeploymentId)?.State != GatewayCircuitState.Open);
        var status = available switch
        {
            > 0 when available == enabled.Length => ModelGatewayHealthStatus.Healthy,
            > 0 => ModelGatewayHealthStatus.Degraded,
            _ => ModelGatewayHealthStatus.Unavailable
        };
        return new ModelGatewayHealth(
            GatewayId,
            status,
            0,
            clock.GetUtcNow(),
            $"{available} of {enabled.Length} registered generic gateway deployments are circuit-eligible.");
    }

    private async Task<FailureObservation> RecordFailureAsync(
        GatewayDeployment deployment,
        GatewayCircuitPermit permit,
        int attempt,
        long durationMilliseconds,
        ModelGatewayException exception,
        CancellationToken cancellationToken)
    {
        // Authentication is deployment configuration, not request semantics. A second
        // deployment may have valid credentials even when the first one returned 401/403.
        var failureClass = exception.FailureKind == ModelGatewayFailureKind.Authentication
            ? GatewayFailureClass.Provider
            : exception.FailureClass ?? Classify(exception);
        var after = await circuits.RecordFailureAsync(
            permit,
            failureClass,
            CountsTowardCircuit(failureClass),
            exception.RetryAfter,
            options.Circuit,
            clock.GetUtcNow(),
            cancellationToken);
        return new FailureObservation(
            new GatewayAttemptEvidence(
                deployment.ToReference(),
                attempt,
                permit.State,
                after.State,
                false,
                failureClass,
                exception.Code,
                exception.StatusCode,
                durationMilliseconds,
                exception.RetryAfter is { } retryAfter
                    ? checked((int)Math.Clamp(retryAfter.TotalMilliseconds, 0, int.MaxValue))
                    : null),
            after);
    }

    private static void EnsureUsableResult(
        ModelGatewayRequest request,
        GatewayDeployment deployment,
        ModelGatewayResult result)
    {
        if (result.Output.ValueKind is System.Text.Json.JsonValueKind.Undefined or
            System.Text.Json.JsonValueKind.Null ||
            result.Confidence is < 0m or > 1m ||
            result.Evidence is null ||
            result.Usage.InputTokens < 0 ||
            result.Usage.OutputTokens < 0 ||
            result.Usage.Cost < 0m ||
            result.Usage.DurationMilliseconds < 0 ||
            result.Usage.ModelCalls is < 0 or > 1 ||
            result.Usage.ToolCalls < 0 ||
            !Enum.IsDefined(result.FinishReason))
        {
            throw new ModelGatewayException(
                ErrorCodes.ModelGatewayInvalidResponse,
                "The generic gateway deployment returned a malformed output or retried outside the bounded resilience owner.",
                true,
                failureKind: ModelGatewayFailureKind.InvalidResponse,
                gatewayId: deployment.GatewayId,
                correlationId: request.CorrelationId,
                failureClass: GatewayFailureClass.MalformedOutput);
        }

        if (request.MinimumQuality is { } qualityFloor && result.Confidence < qualityFloor)
        {
            throw new ModelGatewayException(
                ErrorCodes.ValidationFailed,
                $"The deployment confidence {result.Confidence:0.###} is below the {qualityFloor:0.###} task quality floor.",
                true,
                failureKind: ModelGatewayFailureKind.InvalidResponse,
                gatewayId: deployment.GatewayId,
                correlationId: request.CorrelationId,
                failureClass: GatewayFailureClass.Validation);
        }
    }

    private static string? StaticRejection(GatewayDeployment deployment, ModelGatewayRequest request)
    {
        if (!deployment.Enabled)
        {
            return "The registered deployment is disabled.";
        }
        if (!deployment.Supports(request.Capability, request.ProfileId))
        {
            return "The deployment does not serve the selected capability and model profile.";
        }
        if (request.MinimumQuality is { } quality && deployment.ExpectedQuality < quality)
        {
            return $"Expected quality {deployment.ExpectedQuality:0.###} is below floor {quality:0.###}.";
        }
        if (request.MaximumCost is { } cost && deployment.EstimatedCost > cost)
        {
            return $"Estimated cost exceeds the {cost:0.####} ceiling.";
        }
        if (request.DeadlineMilliseconds is { } deadline &&
            deployment.ExpectedLatencyMilliseconds > deadline)
        {
            return $"Expected latency exceeds the {deadline} ms deadline.";
        }
        if (request.AllowedRegions is { Count: > 0 } regions &&
            !regions.Contains(deployment.Region, StringComparer.OrdinalIgnoreCase))
        {
            return $"Region '{deployment.Region}' is not allowed by task policy.";
        }
        if (!string.IsNullOrWhiteSpace(request.RequiredResidency) &&
            !string.Equals(
                request.RequiredResidency,
                deployment.Residency,
                StringComparison.OrdinalIgnoreCase))
        {
            return $"Residency '{deployment.Residency}' does not satisfy required residency '{request.RequiredResidency}'.";
        }
        return null;
    }

    private static GatewayCandidateEvidence Rejected(
        GatewayDeployment deployment,
        IReadOnlyDictionary<string, GatewayCircuitSnapshot> circuits,
        string reason)
    {
        var state = circuits.GetValueOrDefault(deployment.DeploymentId)?.State ?? GatewayCircuitState.Closed;
        return new GatewayCandidateEvidence(
            deployment.ToReference(),
            false,
            reason,
            state,
            GatewayFailureClass.Policy);
    }

    private static int RemainingMilliseconds(ModelGatewayRequest request, Stopwatch stopwatch)
    {
        var deadline = request.DeadlineMilliseconds ?? int.MaxValue;
        return checked((int)Math.Max(0, Math.Min(int.MaxValue, deadline - stopwatch.ElapsedMilliseconds)));
    }

    private static GatewayFailureClass Classify(ModelGatewayException exception) => exception.FailureKind switch
    {
        ModelGatewayFailureKind.Timeout => GatewayFailureClass.Timeout,
        ModelGatewayFailureKind.RateLimited => GatewayFailureClass.Throttling,
        ModelGatewayFailureKind.Unavailable when exception.StatusCode >= 500 => GatewayFailureClass.Provider,
        ModelGatewayFailureKind.Unavailable => GatewayFailureClass.Transport,
        ModelGatewayFailureKind.InvalidResponse => GatewayFailureClass.MalformedOutput,
        ModelGatewayFailureKind.Authentication => GatewayFailureClass.Provider,
        ModelGatewayFailureKind.InvalidRequest or
        ModelGatewayFailureKind.Internal => GatewayFailureClass.Permanent,
        ModelGatewayFailureKind.Cancelled => GatewayFailureClass.Permanent,
        _ => GatewayFailureClass.Permanent
    };

    private static bool CountsTowardCircuit(GatewayFailureClass failureClass) => failureClass is
        GatewayFailureClass.Timeout or
        GatewayFailureClass.Throttling or
        GatewayFailureClass.Transport or
        GatewayFailureClass.Provider or
        GatewayFailureClass.MalformedOutput or
        GatewayFailureClass.Validation;

    private static string FallbackReason(
        GatewayDeployment deployment,
        GatewayAttemptEvidence attempt) =>
        $"Fallback from deployment '{deployment.DeploymentId}' after {attempt.FailureClass}: {attempt.FailureCode}.";

    private sealed record FailureObservation(
        GatewayAttemptEvidence Attempt,
        GatewayCircuitSnapshot Circuit);
}
