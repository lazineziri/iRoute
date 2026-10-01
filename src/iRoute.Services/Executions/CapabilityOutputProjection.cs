using System.Text.Json;
using iRoute.Common;
using static iRoute.Services.ExecutionSerialization;

namespace iRoute.Services;

internal static class CapabilityOutputProjection
{
    internal static JsonElement AddProjectedCapabilityOutputs(
        TaskRequest request,
        TaskDefinition definition,
        CompiledContext context,
        IReadOnlyDictionary<string, JsonElement> dependencyOutputs)
    {
        if (dependencyOutputs.Count == 0)
        {
            return context.Content;
        }

        var projected = new SortedDictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var dependency in dependencyOutputs)
        {
            var result = dependency.Value.Deserialize<ModelGatewayResult>(ContractJsonOptions)
                ?? throw new WorkflowStepExecutionException(
                    dependency.Key,
                    $"Dependency '{dependency.Key}' has an invalid normalized result envelope.");
            if (result.Usage.ToolCalls > 0)
            {
                projected[dependency.Key] = result.Output.Clone();
            }
        }

        if (projected.Count == 0)
        {
            return context.Content;
        }

        var combined = new SortedDictionary<string, JsonElement>(StringComparer.Ordinal);
        if (context.Content.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in context.Content.EnumerateObject())
            {
                combined[property.Name] = property.Value.Clone();
            }
        }

        combined["capabilityOutputs"] = JsonSerializer.SerializeToElement(projected, ContractJsonOptions);
        var serialized = JsonSerializer.SerializeToElement(combined, ContractJsonOptions);
        var budget = Math.Max(1, request.Constraints?.MaxInputTokens ?? definition.DefaultMaxInputTokens);
        var estimated = TokenEstimator.Estimate(context.ProjectedInput) + TokenEstimator.Estimate(serialized);
        if (estimated > budget)
        {
            throw new ContextCompilationException(
                ErrorCodes.ContextBudgetExceeded,
                "Capability context budget exceeded",
                $"Projected capability output would require {estimated} estimated tokens, above the task limit of {budget}.");
        }

        return serialized;
    }


}
