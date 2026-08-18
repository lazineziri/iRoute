using System.Text.Json;
using iRoute.Common;

namespace iRoute.Services;

public sealed partial class ExecutionService
{
    private async Task<ModelGatewayResult> ExecuteCapabilityStepAsync(
        ExecutionSnapshot snapshot,
        TaskRequest request,
        TaskDefinition definition,
        ExecutionPlanStep step,
        CancellationToken cancellationToken)
    {
        await AppendEventAsync(
            snapshot.ExecutionId,
            ExecutionEventTypes.CapabilityStarted,
            new
            {
                stepId = step.Id,
                step.Capability,
                version = 1,
                sideEffectClass = step.SideEffectClass,
                deadlineMilliseconds = step.TimeoutMilliseconds
            },
            cancellationToken);
        try
        {
            var result = await capabilityExecutor.ExecuteAsync(
                new CapabilityInvocationRequest(
                    step.Capability,
                    1,
                    request.Input,
                    snapshot.TenantId,
                    snapshot.ActorId,
                    request.ProjectId,
                    request.PermissionScopes ?? [],
                    TaskPolicyEngine.CurrentPolicyVersion,
                    step.SideEffectClass,
                    step.TimeoutMilliseconds,
                    MaximumCapabilityOutputBytes(request, definition),
                    snapshot.ExecutionId.ToString()),
                cancellationToken);
            await AppendEventAsync(
                snapshot.ExecutionId,
                ExecutionEventTypes.CapabilityCompleted,
                new
                {
                    stepId = step.Id,
                    capability = result.Metadata.Capability,
                    version = result.Metadata.Version,
                    connectorId = result.Metadata.ConnectorId,
                    kind = result.Metadata.Kind,
                    trustLevel = result.Metadata.TrustLevel,
                    transport = result.Metadata.Transport,
                    projected = result.Metadata.Projected,
                    outputReference = result.Metadata.OutputReference,
                    durationMilliseconds = result.Usage.DurationMilliseconds,
                    toolCalls = result.Usage.ToolCalls
                },
                CancellationToken.None);
            return new ModelGatewayResult(
                result.Output,
                result.Usage,
                result.Confidence,
                result.Evidence);
        }
        catch (CapabilityInvocationException exception)
        {
            var failure = exception.ToFailure();
            await AppendEventAsync(
                snapshot.ExecutionId,
                ExecutionEventTypes.CapabilityFailed,
                new
                {
                    stepId = step.Id,
                    code = failure.Code,
                    kind = failure.Kind,
                    retryable = failure.Retryable,
                    capability = failure.Capability,
                    connectorId = failure.ConnectorId
                },
                CancellationToken.None);
            throw;
        }
    }

    private static JsonElement AddProjectedCapabilityOutputs(
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

    private static int MaximumCapabilityOutputBytes(
        TaskRequest request,
        TaskDefinition definition)
    {
        var tokenLimit = Math.Clamp(
            request.Constraints?.MaxOutputTokens ?? definition.DefaultMaxOutputTokens,
            256,
            16 * 1024);
        return tokenLimit * 4;
    }

}
