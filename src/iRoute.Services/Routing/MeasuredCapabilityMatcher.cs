using iRoute.Common;

namespace iRoute.Services;

public sealed class MeasuredCapabilityMatcher(IModelProfileRegistry modelProfiles) : ICapabilityMatcher
{
    public async Task<CapabilityMatchResult> MatchAsync(
        TaskRequest request,
        TaskDefinition definition,
        string capability,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var profiles = await modelProfiles.ListAsync(capability, cancellationToken);
        if (profiles.Count > 0)
        {
            return new CapabilityMatchResult(
                capability,
                profiles
                    .Select(profile => EvaluateModel(request, definition, profile))
                    .OrderBy(item => item.ExpectedCost)
                    .ThenBy(item => item.ExpectedLatencyMilliseconds)
                    .ThenByDescending(item => item.ExpectedQuality)
                    .ToArray());
        }

        return new CapabilityMatchResult(
            capability,
            [EvaluateNonModel(request, definition, capability)]);
    }

    private static CapabilityCandidate EvaluateModel(
        TaskRequest request,
        TaskDefinition definition,
        ModelProfile profile)
    {
        var failures = new List<string>();
        var qualityFloor = RoutingBudgets.QualityFloor(request, definition);
        var maxInputTokens = RoutingBudgets.MaximumInputTokens(request, definition);
        var maxOutputTokens = RoutingBudgets.MaximumOutputTokens(request, definition);
        var deadline = RoutingBudgets.Deadline(request, definition);
        var maxCost = RoutingBudgets.MaximumCost(request, definition);
        if (!definition.EffectiveAllowedCapabilities.Contains(profile.Capability, StringComparer.Ordinal))
        {
            failures.Add("capability is not allow-listed");
        }
        if (!profile.SupportedTaskTypes.Contains(definition.TaskType, StringComparer.Ordinal))
        {
            failures.Add("task type has no measured profile");
        }
        if (!profile.Healthy || profile.Availability <= 0m)
        {
            failures.Add("profile is unavailable");
        }
        if (profile.ExpectedQuality < qualityFloor)
        {
            failures.Add($"expected quality {profile.ExpectedQuality:0.###} is below floor {qualityFloor:0.###}");
        }
        if (profile.ExpectedLatencyMilliseconds > deadline)
        {
            failures.Add($"expected latency exceeds the {deadline} ms deadline");
        }
        if (profile.MaxInputTokens < maxInputTokens || profile.MaxOutputTokens < maxOutputTokens)
        {
            failures.Add("profile token capacity is below the task budget");
        }
        if (maxCost is { } cost && profile.EstimatedCost > cost)
        {
            failures.Add($"estimated cost exceeds the {cost:0.####} ceiling");
        }
        if (RoutingBudgets.MaximumModelCalls(request, definition) == 0)
        {
            failures.Add("model-call budget is zero");
        }
        ValidateMeasurementProvenance(profile, failures);

        return new CapabilityCandidate(
            profile.Capability,
            ExecutionStepKind.Model,
            SideEffectClass.None,
            profile.ProfileId,
            profile.Tier,
            failures.Count == 0,
            failures.Count == 0
                ? $"eligible {profile.MeasurementSource.ToString().ToLowerInvariant()} model profile"
                : string.Join("; ", failures),
            profile.ExpectedQuality,
            profile.EstimatedCost,
            profile.ExpectedLatencyMilliseconds,
            profile.Uncertainty,
            profile.Reliability,
            profile.Availability,
            Score(definition.EffectiveRoutingWeights, profile),
            profile.MeasurementSource,
            profile.Measurement);
    }

    private static void ValidateMeasurementProvenance(
        ModelProfile profile,
        List<string> failures)
    {
        if (profile.MeasurementSource != ModelProfileSource.Measured)
        {
            if (profile.Measurement is not null)
            {
                failures.Add("only measured profiles may carry measurement metadata");
            }
            return;
        }

        if (profile.Measurement is null)
        {
            failures.Add("measured profile is missing measurement metadata");
            return;
        }

        if (string.IsNullOrWhiteSpace(profile.Measurement.Provider) ||
            string.IsNullOrWhiteSpace(profile.Measurement.Model) ||
            profile.Measurement.MeasuredAt == default ||
            profile.Measurement.SampleCount <= 0)
        {
            failures.Add("measured profile has invalid measurement metadata");
        }
    }

    private static CapabilityCandidate EvaluateNonModel(
        TaskRequest request,
        TaskDefinition definition,
        string capability)
    {
        var failures = new List<string>();
        if (!definition.EffectiveAllowedCapabilities.Contains(capability, StringComparer.Ordinal))
        {
            failures.Add("capability is not allow-listed");
        }
        if (RoutingBudgets.MaximumToolCalls(request, definition) == 0)
        {
            failures.Add("tool-call budget is zero");
        }

        const decimal expectedQuality = 1m;
        const decimal estimatedCost = 0m;
        const int expectedLatency = 25;
        const decimal uncertainty = 0m;
        var weights = definition.EffectiveRoutingWeights;
        var score = weights.Quality * expectedQuality -
            weights.Cost * estimatedCost -
            weights.Latency * expectedLatency -
            weights.Uncertainty * uncertainty;
        return new CapabilityCandidate(
            capability,
            ExecutionStepKind.Tool,
            definition.SideEffectClass,
            null,
            null,
            failures.Count == 0,
            failures.Count == 0 ? "eligible registered non-model capability" : string.Join("; ", failures),
            expectedQuality,
            estimatedCost,
            expectedLatency,
            uncertainty,
            1m,
            1m,
            score);
    }

    private static decimal Score(RoutingWeights weights, ModelProfile profile) =>
        weights.Quality * profile.ExpectedQuality -
        weights.Cost * profile.EstimatedCost -
        weights.Latency * profile.ExpectedLatencyMilliseconds -
        weights.Uncertainty * profile.Uncertainty;
}
