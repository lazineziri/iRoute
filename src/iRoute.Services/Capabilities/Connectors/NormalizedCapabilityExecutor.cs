using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using iRoute.Common;

namespace iRoute.Services;

public sealed class NormalizedCapabilityExecutor(
    ICapabilityDefinitionRegistry definitions,
    IEnumerable<ICapabilityConnector> connectors) : ICapabilityExecutor
{
    public async Task<CapabilityInvocationResult> ExecuteAsync(
        CapabilityInvocationRequest request,
        CancellationToken cancellationToken)
    {
        ValidateRequest(request);
        var definition = await definitions.FindAsync(
            request.Capability,
            request.Version,
            cancellationToken) ?? throw Failure(
                ErrorCodes.CapabilityNotRegistered,
                $"Capability '{request.Capability}' version {request.Version} is not registered.",
                CapabilityFailureKind.NotRegistered,
                request);
        if (definition.SideEffectClass != request.SideEffectClass)
        {
            throw Failure(
                ErrorCodes.CapabilityContractMismatch,
                $"Capability '{request.Capability}' was requested as {request.SideEffectClass} but is registered as {definition.SideEffectClass}.",
                CapabilityFailureKind.InvalidRequest,
                request);
        }

        var granted = new HashSet<string>(request.PermissionScopes, StringComparer.Ordinal);
        var missing = definition.PermissionScopes.Where(scope => !granted.Contains(scope)).ToArray();
        if (missing.Length > 0)
        {
            throw Failure(
                ErrorCodes.PermissionScopeDenied,
                $"Capability '{request.Capability}' requires permission scope(s): {string.Join(", ", missing)}.",
                CapabilityFailureKind.PermissionDenied,
                request);
        }

        if (definition.SideEffectClass >= SideEffectClass.ReversibleWrite &&
            definition.Idempotency.RequiredForWrites &&
            string.IsNullOrWhiteSpace(request.IdempotencyReference))
        {
            throw Failure(
                ErrorCodes.ExternalActionIdempotencyRequired,
                $"Capability '{request.Capability}' requires an idempotency reference.",
                CapabilityFailureKind.InvalidRequest,
                request);
        }

        var matches = connectors.Where(item => item.Supports(definition)).ToArray();
        if (matches.Length != 1)
        {
            throw Failure(
                ErrorCodes.CapabilityNotRegistered,
                matches.Length == 0
                    ? $"Capability '{request.Capability}' has no connector."
                    : $"Capability '{request.Capability}' has multiple connectors.",
                CapabilityFailureKind.NotRegistered,
                request);
        }

        var connector = matches[0];
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromMilliseconds(request.DeadlineMilliseconds));
        var stopwatch = Stopwatch.StartNew();
        CapabilityConnectorResult connectorResult;
        try
        {
            connectorResult = await connector.InvokeAsync(request, definition, timeout.Token);
        }
        catch (CapabilityInvocationException)
        {
            throw;
        }
        catch (OperationCanceledException) when (
            timeout.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            throw new CapabilityInvocationException(
                ErrorCodes.CapabilityDeadlineExceeded,
                $"Capability '{request.Capability}' exceeded its {request.DeadlineMilliseconds} millisecond deadline.",
                CapabilityFailureKind.Timeout,
                true,
                request.Capability,
                connector.ConnectorId,
                request.CorrelationId);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            throw new CapabilityInvocationException(
                ErrorCodes.CapabilityInvocationFailed,
                $"Capability '{request.Capability}' failed: {exception.Message}",
                CapabilityFailureKind.Internal,
                false,
                request.Capability,
                connector.ConnectorId,
                request.CorrelationId,
                exception);
        }

        stopwatch.Stop();
        if (connectorResult.ProjectedOutput.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined ||
            connectorResult.Confidence is < 0m or > 1m)
        {
            throw new CapabilityInvocationException(
                ErrorCodes.CapabilityResultInvalid,
                $"Connector '{connector.ConnectorId}' returned an invalid normalized result.",
                CapabilityFailureKind.InvalidResponse,
                false,
                request.Capability,
                connector.ConnectorId,
                request.CorrelationId);
        }

        var serializedOutput = JsonSerializer.SerializeToUtf8Bytes(connectorResult.ProjectedOutput);
        if (serializedOutput.Length > request.MaximumOutputBytes)
        {
            throw new CapabilityInvocationException(
                ErrorCodes.CapabilityOutputLimitExceeded,
                $"Capability '{request.Capability}' returned {serializedOutput.Length} bytes, above the {request.MaximumOutputBytes} byte limit.",
                CapabilityFailureKind.OutputLimitExceeded,
                false,
                request.Capability,
                connector.ConnectorId,
                request.CorrelationId);
        }

        var outputReference = Convert.ToHexStringLower(SHA256.HashData(serializedOutput));
        return new CapabilityInvocationResult(
            connectorResult.ProjectedOutput.Clone(),
            new UsageSummary(DurationMilliseconds: stopwatch.ElapsedMilliseconds, ToolCalls: 1),
            connectorResult.Confidence,
            connectorResult.Evidence
                .DistinctBy(item => (item.Kind, item.Reference, item.ContentHash))
                .ToArray(),
            new CapabilityExecutionMetadata(
                definition.Capability,
                definition.Version,
                connector.ConnectorId,
                definition.Kind,
                definition.TrustLevel,
                connector.Transport,
                true,
                outputReference));
    }

    private static void ValidateRequest(CapabilityInvocationRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Capability) ||
            request.Version < 1 ||
            string.IsNullOrWhiteSpace(request.TenantId) ||
            string.IsNullOrWhiteSpace(request.ActorId) ||
            string.IsNullOrWhiteSpace(request.PolicyVersion) ||
            string.IsNullOrWhiteSpace(request.CorrelationId) ||
            request.DeadlineMilliseconds < 1 ||
            request.MaximumOutputBytes < 1)
        {
            throw Failure(
                ErrorCodes.InvalidTaskRequest,
                "The capability invocation envelope is invalid.",
                CapabilityFailureKind.InvalidRequest,
                request);
        }
    }

    private static CapabilityInvocationException Failure(
        string code,
        string message,
        CapabilityFailureKind kind,
        CapabilityInvocationRequest request) => new(
            code,
            message,
            kind,
            false,
            request.Capability,
            correlationId: request.CorrelationId);
}
