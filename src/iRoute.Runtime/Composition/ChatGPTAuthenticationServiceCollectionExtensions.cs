using iRoute.Common;
using iRoute.Data;
using iRoute.Services;

namespace iRoute.Runtime.Composition;

internal static class ChatGPTAuthenticationServiceCollectionExtensions
{
    public static void AddIRouteChatGPTAuthentication(this IServiceCollection services)
    {
        services.AddSingleton<IChatGPTCredentialStore, ChatGPTFileCredentialStore>();
        services.AddHttpClient<OpenAIAuthenticationClient>(client => client.Timeout = TimeSpan.FromSeconds(30))
            .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false })
            .RemoveAllLoggers();
        services.AddSingleton<OpenAIIdentityValidator>();
        services.AddSingleton<ChatGPTAuthorizationService>();
        services.AddSingleton<ChatGPTSessionService>();
        services.AddSingleton<IChatGPTAccessTokenProvider>(provider => provider.GetRequiredService<ChatGPTSessionService>());
    }
}
