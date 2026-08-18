using System.Diagnostics;
using System.Text.Json;
using iRoute.Common;

namespace iRoute.Services;

public sealed partial class ExecutionService
{
    private async Task<ModelGatewayResult> ExecuteModelStepAsync(
        Guid executionId,
        TaskRequest request,
        TaskDefinition definition,
        CompiledContext context,
        ExecutionPlanStep step,
        ModelStepGatewayBudget gatewayBudget,
        IReadOnlyDictionary<string, JsonElement> dependencyOutputs,
        CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        var modelContext = AddProjectedCapabilityOutputs(
            request,
            definition,
            context,
            dependencyOutputs);
        var gatewayRequest = new ModelGatewayRequest(
            step.Capability,
            context.ProjectedInput,
            modelContext,
            request.Constraints?.MaxOutputTokens ?? definition.DefaultMaxOutputTokens,
            executionId.ToString(),
            step.ProfileId,
            step.TimeoutMilliseconds,
            MinimumQuality: Math.Max(
                request.Constraints?.MinimumQuality ?? definition.MinimumQuality,
                definition.MinimumQuality),
            MaximumCost: gatewayBudget.MaximumCost,
            AllowedRegions: request.Constraints?.AllowedRegions,
            RequiredResidency: request.Constraints?.RequiredResidency,
            MaximumAttempts: gatewayBudget.MaximumAttempts);
        await AppendEventAsync(
            executionId,
            ExecutionEventTypes.GatewayStarted,
            new
            {
                stepId = step.Id,
                capability = step.Capability,
                step.ProfileId,
                gatewayId = modelGateway.GatewayId,
                deadlineMilliseconds = step.TimeoutMilliseconds
            },
            cancellationToken);

        var streamEvents = 0;
        var deltaEvents = 0;
        var deltaCharacters = 0;
        var usageUpdates = 0;
        var previousSequence = 0L;
        ModelGatewayResult? result = null;
        try
        {
            await foreach (var streamEvent in modelGateway.StreamAsync(
                gatewayRequest,
                cancellationToken))
            {
                streamEvents++;
                if (streamEvent.Sequence <= previousSequence)
                {
                    throw InvalidGatewayStream(
                        gatewayRequest,
                        "Model gateway stream event sequences must increase monotonically.");
                }

                previousSequence = streamEvent.Sequence;
                switch (streamEvent.Kind)
                {
                    case ModelGatewayStreamEventKind.OutputDelta when streamEvent.Delta is { } delta:
                        deltaEvents++;
                        deltaCharacters = checked(deltaCharacters + delta.Length);
                        break;
                    case ModelGatewayStreamEventKind.Usage when streamEvent.Usage is not null:
                        usageUpdates++;
                        break;
                    case ModelGatewayStreamEventKind.Completed when
                        streamEvent.Result is not null && result is null:
                        result = streamEvent.Result;
                        break;
                    default:
                        throw InvalidGatewayStream(
                            gatewayRequest,
                            $"The model gateway emitted an invalid {streamEvent.Kind} event.");
                }
            }

            cancellationToken.ThrowIfCancellationRequested();

            if (result is null)
            {
                throw InvalidGatewayStream(
                    gatewayRequest,
                    "The model gateway stream ended without a completed result.");
            }
        }
        catch (ModelGatewayException exception)
        {
            stopwatch.Stop();
            if (exception.Resilience is { } resilience)
            {
                await AppendGatewayResilienceEvidenceAsync(
                    executionId,
                    resilience,
                    CancellationToken.None);
            }
            await AppendGatewayFailureAsync(
                executionId,
                step,
                exception.ToFailure(),
                stopwatch.ElapsedMilliseconds);
            throw;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            stopwatch.Stop();
            await AppendGatewayFailureAsync(
                executionId,
                step,
                new ModelGatewayFailure(
                    ErrorCodes.ExecutionCancelled,
                    ModelGatewayFailureKind.Cancelled,
                    "The model gateway call was cancelled.",
                    false,
                    GatewayId: modelGateway.GatewayId,
                    CorrelationId: gatewayRequest.CorrelationId),
                stopwatch.ElapsedMilliseconds);
            throw;
        }

        stopwatch.Stop();
        var normalized = result with
        {
            Usage = result.Usage with
            {
                DurationMilliseconds = stopwatch.ElapsedMilliseconds,
                ModelCalls = Math.Max(1, result.Usage.ModelCalls)
            }
        };
        if (normalized.Resilience is { } resilienceTrace)
        {
            await AppendGatewayResilienceEvidenceAsync(
                executionId,
                resilienceTrace,
                cancellationToken);
        }
        if (normalized.Transport == ModelGatewayTransport.Streaming)
        {
            await AppendEventAsync(
                executionId,
                ExecutionEventTypes.GatewayStreamed,
                new
                {
                    stepId = step.Id,
                    gatewayId = normalized.GatewayId,
                    streamEvents,
                    deltaEvents,
                    deltaCharacters,
                    usageUpdates
                },
                cancellationToken);
        }

        return normalized;
    }

}
