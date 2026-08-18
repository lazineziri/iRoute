
namespace iRoute.Common;

public interface IContextCompiler
{
    Task<CompiledContext> CompileAsync(
        TaskRequest request,
        TaskDefinition definition,
        CancellationToken cancellationToken);
}

public interface ITaskOutcomeValidator
{
    bool Supports(string taskType);
    Task<OutcomeValidationResult> ValidateAsync(
        TaskRequest request,
        TaskDefinition definition,
        ModelGatewayResult result,
        CompiledContext context,
        CancellationToken cancellationToken);
}

public interface IInputFingerprint
{
    string Create(TaskRequest request, int taskDefinitionVersion);

    /// <summary>
    /// Fingerprints a request as submitted, before any task definition has been resolved, so two
    /// submissions carrying the same idempotency key can be compared.
    /// </summary>
    string CreateForSubmission(TaskRequest request);
}

public interface IExecutionCancellationRegistry
{
    CancellationToken Register(Guid executionId, CancellationToken requestCancellation);
    bool RequestCancellation(Guid executionId);
    void Complete(Guid executionId);
}
