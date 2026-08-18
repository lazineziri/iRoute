using iRoute.Common;

namespace iRoute.Services;

public sealed class MeasuredEscalationPolicy : IEscalationPolicy
{
    public CapabilitySelection SelectCandidate(
        string capability,
        decimal qualityFloor,
        IReadOnlyList<CapabilityCandidate> candidates)
    {
        var ordered = candidates
            .OrderBy(item => item.ExpectedCost)
            .ThenBy(item => item.ExpectedLatencyMilliseconds)
            .ThenByDescending(item => item.ExpectedQuality)
            .ToArray();
        var selected = ordered.FirstOrDefault(item => item.Eligible);
        if (selected is null)
        {
            var reasons = string.Join(
                " | ",
                ordered.Select(item => $"{item.ProfileId ?? item.Capability}: {item.Reason}"));
            throw new RoutingException(
                ordered.Any(item => item.StepKind == ExecutionStepKind.Model &&
                    item.Reason.Contains("model-call budget is zero", StringComparison.Ordinal))
                    ? ErrorCodes.ModelBudgetExhausted
                    : ErrorCodes.RoutingNoEligibleCapability,
                "No eligible capability",
                $"No measured route for '{capability}' satisfies quality floor {qualityFloor:0.###} and task policy. {reasons}".Trim());
        }

        var bypassed = ordered.TakeWhile(item => !ReferenceEquals(item, selected)).ToArray();
        if (bypassed.Length == 0)
        {
            return new CapabilitySelection(selected, false, null);
        }

        var first = bypassed[0];
        return new CapabilitySelection(
            selected,
            true,
            $"Escalated past lower-cost route '{first.ProfileId ?? first.Capability}' because {first.Reason}.");
    }
}
