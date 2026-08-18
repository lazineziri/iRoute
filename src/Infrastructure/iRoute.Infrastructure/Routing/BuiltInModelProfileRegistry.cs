using iRoute.Contracts;
using iRoute.Core;

namespace iRoute.Infrastructure;

public sealed class BuiltInModelProfileRegistry : IModelProfileRegistry
{
    private static readonly IReadOnlyList<ModelProfile> Profiles =
    [
        new(
            "text.generation.small.eval-v1",
            "text.generation",
            ModelTier.Small,
            ["email.draft"],
            0.84m,
            0.004m,
            900,
            0.06m,
            0.98m,
            0.99m,
            8_000,
            1_500,
            MeasurementSource: ModelProfileSource.Synthetic),
        new(
            "text.generation.strong.eval-v1",
            "text.generation",
            ModelTier.Strong,
            ["email.draft"],
            0.94m,
            0.020m,
            2_200,
            0.03m,
            0.99m,
            0.995m,
            32_000,
            4_000,
            MeasurementSource: ModelProfileSource.Synthetic),
        new(
            "text.summarization.small.eval-v1",
            "text.summarization",
            ModelTier.Small,
            ["document.summarize"],
            0.87m,
            0.006m,
            1_100,
            0.06m,
            0.98m,
            0.99m,
            12_000,
            1_500,
            MeasurementSource: ModelProfileSource.Synthetic),
        new(
            "text.summarization.strong.eval-v1",
            "text.summarization",
            ModelTier.Strong,
            ["document.summarize"],
            0.95m,
            0.025m,
            2_800,
            0.03m,
            0.99m,
            0.995m,
            64_000,
            4_000,
            MeasurementSource: ModelProfileSource.Synthetic)
    ];

    public Task<IReadOnlyList<ModelProfile>> ListAsync(
        string capability,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult<IReadOnlyList<ModelProfile>>(Profiles
            .Where(item => string.Equals(item.Capability, capability, StringComparison.Ordinal))
            .OrderBy(item => item.EstimatedCost)
            .ThenBy(item => item.ExpectedLatencyMilliseconds)
            .ToArray());
    }
}
