using System.Text.Json;

namespace iRoute.Common;

public interface ICapabilityDefinitionRegistry
{
    Task<CapabilityDefinition?> FindAsync(
        string capability,
        int version,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<CapabilityDefinition>> ListAsync(CancellationToken cancellationToken);
}

public interface ICapabilityConnector
{
    string ConnectorId { get; }
    string Transport { get; }
    bool Supports(CapabilityDefinition definition);

    Task<CapabilityConnectorResult> InvokeAsync(
        CapabilityInvocationRequest request,
        CapabilityDefinition definition,
        CancellationToken cancellationToken);
}

public sealed record CapabilityConnectorResult(
    JsonElement ProjectedOutput,
    decimal Confidence,
    IReadOnlyList<EvidenceReference> Evidence);

public interface ICapabilityExecutor
{
    Task<CapabilityInvocationResult> ExecuteAsync(
        CapabilityInvocationRequest request,
        CancellationToken cancellationToken);
}

public sealed class CapabilityInvocationException(
    string code,
    string message,
    CapabilityFailureKind failureKind,
    bool retryable = false,
    string? capability = null,
    string? connectorId = null,
    string? correlationId = null,
    Exception? innerException = null) : Exception(message, innerException)
{
    public string Code { get; } = code;
    public CapabilityFailureKind FailureKind { get; } = failureKind;
    public bool Retryable { get; } = retryable;
    public string? Capability { get; } = capability;
    public string? ConnectorId { get; } = connectorId;
    public string? CorrelationId { get; } = correlationId;

    public CapabilityFailure ToFailure() => new(
        Code,
        FailureKind,
        Message,
        Retryable,
        Capability,
        ConnectorId,
        CorrelationId);
}
