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
        services.AddScoped<ProjectMemoryMaterializer>();
        services.AddSingleton<BoundedDependencyScheduler>();
        services.AddSingleton(schedulerOptions ?? new WorkflowSchedulerOptions());
        services.AddSingleton<IExecutionPlanValidator, ExecutionPlanValidator>();
        services.AddSingleton<IEscalationPolicy, MeasuredEscalationPolicy>();
        services.AddSingleton<ICapabilityMatcher, MeasuredCapabilityMatcher>();
        services.AddSingleton<IDirectPathSelector, DirectPathSelector>();
        services.AddSingleton<IBoundedTaskPlanner, BoundedTaskPlanner>();
        services.AddSingleton<ITaskRouter, TaskRouter>();
        services.AddSingleton<ITaskPolicyEngine, TaskPolicyEngine>();
        services.AddSingleton<IInputFingerprint, Sha256InputFingerprint>();
        services.AddSingleton<IContextCompiler, BoundedContextCompiler>();
        services.AddSingleton<INoModelResolver, ExactResultResolver>();
        services.AddSingleton<INoModelResolver, FactDecisionResolver>();
        services.AddSingleton<INoModelResolver, ArtifactLookupResolver>();
        services.AddSingleton<INoModelResolver, DeterministicHandlerResolver>();
        services.AddSingleton<ITaskOutcomeValidator, EmailDraftOutcomeValidator>();
        services.AddSingleton<ITaskOutcomeValidator, EmailSendOutcomeValidator>();
        services.AddSingleton<ITaskOutcomeValidator, DefaultTaskOutcomeValidator>();
        services.AddSingleton<IExecutionCancellationRegistry, ExecutionCancellationRegistry>();
        services.AddSingleton<IExecutionTelemetry, RuntimeTelemetry>();
        return services;
    }
}
