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
