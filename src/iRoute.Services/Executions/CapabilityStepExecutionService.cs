using iRoute.Common;

namespace iRoute.Services;

public sealed class CapabilityStepExecutionService(
    ICapabilityExecutor capabilityExecutor,
    ExecutionPersistenceService persistence)
{
    internal async Task<ModelGatewayResult> ExecuteCapabilityStepAsync(
        ExecutionSnapshot snapshot,
        TaskRequest request,
        TaskDefinition definition,
        ExecutionPlanStep step,
        CancellationToken cancellationToken)
    {
        await persistence.AppendEventAsync(
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
            await persistence.AppendEventAsync(
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
            await persistence.AppendEventAsync(
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

    internal static int MaximumCapabilityOutputBytes(
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
