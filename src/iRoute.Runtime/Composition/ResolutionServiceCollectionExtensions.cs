using iRoute.Common;
using iRoute.Services;

namespace iRoute.Runtime.Composition;

internal static class ResolutionServiceCollectionExtensions
{
    public static void AddResolutionServices(this IServiceCollection services)
    {
        services.AddSingleton<IInputFingerprint, Sha256InputFingerprint>();
        services.AddSingleton<INoModelResolver, ExactResultResolver>();
        services.AddSingleton<INoModelResolver, FactDecisionResolver>();
        services.AddSingleton<INoModelResolver, ArtifactLookupResolver>();
        services.AddSingleton<INoModelResolver, DeterministicHandlerResolver>();
        services.AddSingleton<ITaskOutcomeValidator, EmailDraftOutcomeValidator>();
        services.AddSingleton<ITaskOutcomeValidator, EmailSendOutcomeValidator>();
        services.AddSingleton<ITaskOutcomeValidator, DefaultTaskOutcomeValidator>();
    }
}
