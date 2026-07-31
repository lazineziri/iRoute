using Microsoft.Extensions.DependencyInjection;
using iRoute.Core;

namespace iRoute.Runtime;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddIRouteRuntime(
        this IServiceCollection services,
        WorkflowSchedulerOptions? schedulerOptions = null)
    {
        services.AddScoped<ExecutionOrchestrator>();
        services.AddSingleton<BoundedDependencyScheduler>();
        services.AddSingleton(schedulerOptions ?? new WorkflowSchedulerOptions());
        services.AddSingleton<IExecutionPlanFactory, DirectExecutionPlanFactory>();
        services.AddSingleton<IExecutionPlanValidator, ExecutionPlanValidator>();
        services.AddSingleton<ITaskPolicyEngine, TaskPolicyEngine>();
        services.AddSingleton<IInputFingerprint, Sha256InputFingerprint>();
        services.AddSingleton<IContextCompiler, BoundedContextCompiler>();
        services.AddSingleton<INoModelResolver, ArtifactReuseResolver>();
        services.AddSingleton<ITaskOutcomeValidator, EmailDraftOutcomeValidator>();
        services.AddSingleton<ITaskOutcomeValidator, EmailSendOutcomeValidator>();
        services.AddSingleton<ITaskOutcomeValidator, DefaultTaskOutcomeValidator>();
        services.AddSingleton<IExecutionCancellationRegistry, ExecutionCancellationRegistry>();
        return services;
    }
}
