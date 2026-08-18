using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using iRoute.Contracts;
using iRoute.Core;
using Microsoft.Extensions.Options;

namespace iRoute.Infrastructure;

public sealed class ConfiguredGatewayDeploymentRegistry : IGatewayDeploymentRegistry
{
    private readonly IReadOnlyList<GatewayDeployment> _deployments;

    public ConfiguredGatewayDeploymentRegistry(IOptions<ModelGatewayOptions> configuredOptions)
    {
        var options = configuredOptions.Value;
        options.Resilience.EnsureValid();
        _deployments = EffectiveOptions(options).Select(ToDeployment).ToArray();
        var duplicateRoute = _deployments
            .GroupBy(item => item.RouteId, StringComparer.Ordinal)
            .FirstOrDefault(group => group.Count() > 1);
        var duplicateDeployment = _deployments
            .GroupBy(item => item.DeploymentId, StringComparer.Ordinal)
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicateRoute is not null || duplicateDeployment is not null)
        {
            throw new InvalidOperationException(
                "ModelGateway deployment route IDs and deployment IDs must be unique.");
        }
    }

    public Task<IReadOnlyList<GatewayDeployment>> ListAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(_deployments);
    }

    internal static IReadOnlyList<ModelGatewayDeploymentOptions> EffectiveOptions(ModelGatewayOptions options) =>
        options.Deployments.Count > 0
            ? options.Deployments
            :
            [
                new ModelGatewayDeploymentOptions
                {
                    RouteId = "legacy",
                    GatewayId = options.GatewayId,
                    DeploymentId = options.GatewayId,
                    Provider = "generic",
                    Region = "unspecified",
                    Residency = "unspecified",
                    ModelVersion = "unspecified",
                    Capabilities = ["*"],
                    ProfileIds = ["*"],
                    ExpectedQuality = 1m,
                    EstimatedCost = 0m,
                    ExpectedLatencyMilliseconds = 0,
                    Priority = 100,
                    Enabled = true,
                    Transport = options.Transport,
                    BaseUrl = options.BaseUrl,
                    ApiKey = options.ApiKey,
                    ExecutePath = options.ExecutePath,
                    StreamPath = options.StreamPath,
                    HealthPath = options.HealthPath
                }
            ];

    private static GatewayDeployment ToDeployment(ModelGatewayDeploymentOptions route)
    {
        var invalidFields = InvalidFields(route);
        if (invalidFields.Count > 0)
        {
            throw new InvalidOperationException(
                $"Generic gateway route '{route.RouteId}' has invalid configuration fields: " +
                string.Join(", ", invalidFields) + ".");
        }

        return new GatewayDeployment(
            route.RouteId,
            route.GatewayId,
            route.Provider,
            route.DeploymentId,
            route.Region,
            route.Residency,
            route.ModelVersion,
            route.Capabilities.ToArray(),
            route.ProfileIds.ToArray(),
            route.ExpectedQuality,
            route.EstimatedCost,
            route.ExpectedLatencyMilliseconds,
            route.Priority,
            route.Enabled);
    }

    private static List<string> InvalidFields(ModelGatewayDeploymentOptions route)
    {
        var invalid = new List<string>();
        if (string.IsNullOrWhiteSpace(route.RouteId)) invalid.Add(nameof(route.RouteId));
        if (string.IsNullOrWhiteSpace(route.GatewayId)) invalid.Add(nameof(route.GatewayId));
        if (string.IsNullOrWhiteSpace(route.DeploymentId)) invalid.Add(nameof(route.DeploymentId));
        if (string.IsNullOrWhiteSpace(route.Provider)) invalid.Add(nameof(route.Provider));
        if (string.IsNullOrWhiteSpace(route.Region)) invalid.Add(nameof(route.Region));
        if (string.IsNullOrWhiteSpace(route.Residency)) invalid.Add(nameof(route.Residency));
        if (string.IsNullOrWhiteSpace(route.ModelVersion)) invalid.Add(nameof(route.ModelVersion));
        if (route.Capabilities.Count == 0 ||
            route.Capabilities.Any(string.IsNullOrWhiteSpace) ||
            route.Capabilities.Distinct(StringComparer.Ordinal).Count() != route.Capabilities.Count)
        {
            invalid.Add(nameof(route.Capabilities));
        }
        if (route.ProfileIds.Count == 0 ||
            route.ProfileIds.Any(string.IsNullOrWhiteSpace) ||
            route.ProfileIds.Distinct(StringComparer.Ordinal).Count() != route.ProfileIds.Count)
        {
            invalid.Add(nameof(route.ProfileIds));
        }
        if (route.ExpectedQuality is < 0m or > 1m) invalid.Add(nameof(route.ExpectedQuality));
        if (route.EstimatedCost < 0m) invalid.Add(nameof(route.EstimatedCost));
        if (route.ExpectedLatencyMilliseconds < 0) invalid.Add(nameof(route.ExpectedLatencyMilliseconds));
        if (route.Priority < 0) invalid.Add(nameof(route.Priority));
        if (route.Enabled && !IsHttpBaseUrl(route.BaseUrl)) invalid.Add(nameof(route.BaseUrl));
        return invalid;
    }

    private static bool IsHttpBaseUrl(string? value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
        (string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) ||
         string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase));
}

public sealed class ConfiguredGatewayDeploymentClientFactory(
    IHttpClientFactory httpClients,
    IOptions<ModelGatewayOptions> configuredOptions,
    TimeProvider clock) : IGatewayDeploymentClientFactory
{
    private readonly ModelGatewayOptions _options = configuredOptions.Value;
    private readonly ConcurrentDictionary<string, IModelGateway> _clients = new(StringComparer.Ordinal);

    public IModelGateway GetClient(GatewayDeployment deployment) =>
        _clients.GetOrAdd(deployment.RouteId, _ => CreateClient(deployment));

    private GenericHttpModelGateway CreateClient(GatewayDeployment deployment)
    {
        var route = ConfiguredGatewayDeploymentRegistry.EffectiveOptions(_options)
            .Single(item => string.Equals(item.RouteId, deployment.RouteId, StringComparison.Ordinal));
        var client = httpClients.CreateClient("iroute-generic-gateway");
        if (Uri.TryCreate(route.BaseUrl, UriKind.Absolute, out var baseUri))
        {
            client.BaseAddress = baseUri;
        }

        return new GenericHttpModelGateway(
            client,
            Options.Create(new ModelGatewayOptions
            {
                Mode = "Http",
                GatewayId = route.GatewayId,
                Transport = route.Transport,
                BaseUrl = route.BaseUrl,
                ApiKey = route.ApiKey,
                ExecutePath = route.ExecutePath,
                StreamPath = route.StreamPath,
                HealthPath = route.HealthPath
            }),
            clock);
    }
}

public sealed class ResilientModelGateway(
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
                InMemoryGatewayCircuitStore.Initial(deployment.DeploymentId, clock.GetUtcNow());
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
