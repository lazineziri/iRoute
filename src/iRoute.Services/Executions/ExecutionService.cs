using System.Text.Json;
using iRoute.Common;

namespace iRoute.Services;

public sealed partial class ExecutionService(
    IExecutionStore store,
    IArtifactStore artifacts,
    ProjectMemoryMaterializer projectMemory,
    IEnumerable<INoModelResolver> resolvers,
    ITaskDefinitionRegistry taskDefinitions,
    ITaskRouter taskRouter,
    IExecutionPlanValidator planValidator,
    ITaskPolicyEngine policyEngine,
    IWorkflowCheckpointStore checkpoints,
    IApprovalStore approvals,
    IExternalActionStore externalActions,
    BoundedDependencyScheduler scheduler,
    ICapabilityExecutor capabilityExecutor,
    IModelGateway modelGateway,
    IExternalActionExecutor externalActionExecutor,
    IContextCompiler contextCompiler,
    IEnumerable<ITaskOutcomeValidator> validators,
    IInputFingerprint fingerprint,
    IExecutionCancellationRegistry cancellations,
    TimeProvider clock,
    IExecutionTelemetry? executionTelemetry = null,
    IExecutionWorkStore? executionWork = null) : IExecutionService
{
    private static readonly JsonSerializerOptions ContractJsonOptions = new(JsonSerializerDefaults.Web);
    private readonly IExecutionTelemetry _telemetry = executionTelemetry ?? NoOpExecutionTelemetry.Instance;

}
