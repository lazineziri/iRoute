using iRoute.Common;
using iRoute.Core;
using iRoute.Services;
using Microsoft.Extensions.Options;

namespace iRoute.Runtime.Composition;

internal static class ExecutionServiceCollectionExtensions
{
    public static void AddExecutionServices(this IServiceCollection services)
    {
        services.AddScoped<ExecutionOrchestrator>();
        services.AddScoped<IExecutionService, ExecutionService>();
        services.AddScoped<ExecutionSubmissionService>();
        services.AddScoped<ExecutionPreparationService>();
        services.AddScoped<ExecutionResolutionService>();
        services.AddScoped<ExecutionApprovalService>();
        services.AddScoped<ApprovalRequestService>();
        services.AddScoped<ApprovedExecutionService>();
        services.AddScoped<QueuedExecutionService>();
        services.AddScoped<PlanExecutionService>();
        services.AddScoped<ModelStepExecutionService>();
        services.AddScoped<CapabilityStepExecutionService>();
        services.AddScoped<ExternalActionExecutionService>();
        services.AddScoped<ExecutionOutcomeService>();
        services.AddScoped<ProjectStateExecutionService>();
        services.AddScoped<ExecutionPersistenceService>();
        services.AddScoped<GatewayEvidenceService>();
        services.AddScoped<ExecutionCancellationService>();
        services.AddScoped<ExternalActionReconciliationService>();
        services.AddScoped<ProjectMemoryMaterializer>();
        services.AddSingleton<BoundedDependencyScheduler>();
        services.AddSingleton<IValidateOptions<WorkflowSchedulerOptions>, WorkflowSchedulerOptionsValidator>();
        services.AddSingleton(provider =>
            provider.GetRequiredService<IOptions<WorkflowSchedulerOptions>>().Value);
        services.AddSingleton<IExecutionPlanValidator, ExecutionPlanValidator>();
        services.AddSingleton<ITaskPolicyEngine, TaskPolicyEngine>();
        services.AddSingleton<IExecutionCancellationRegistry, ExecutionCancellationRegistry>();
        services.AddSingleton<IExecutionTelemetry, RuntimeTelemetry>();
    }
}
