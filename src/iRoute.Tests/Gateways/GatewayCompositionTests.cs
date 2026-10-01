using iRoute.Common;
using iRoute.Runtime.Composition;
using iRoute.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Xunit;

namespace iRoute.Tests.Gateways;

public sealed class GatewayCompositionTests
{
    [Theory]
    [InlineData("OpenAI", typeof(OpenAIModelGateway))]
    [InlineData("Anthropic", typeof(AnthropicModelGateway))]
    [InlineData("OpenAIChatGPT", typeof(OpenAIModelGateway))]
    [InlineData("ClaudeCode", typeof(ClaudeCodeSubscriptionGateway))]
    public void ConfiguredModesActuallyResolveTheSelectedAdapter(string mode, Type expected)
    {
        using var host = CreateHost(mode, "Development");
        Assert.IsType(expected, host.Services.GetRequiredService<IModelGateway>());
    }

    [Theory]
    [InlineData("ClaudeCode")]
    [InlineData("OpenAIChatGPT")]
    public void HostedEnvironmentsRejectPersonalSubscriptionCredentials(string mode)
    {
        using var host = CreateHost(mode, "Production");
        var error = Assert.Throws<OptionsValidationException>(() => host.Services.GetRequiredService<IOptions<ModelGatewayOptions>>().Value);
        Assert.Contains("local-development only", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("low")]
    [InlineData("max")]
    [InlineData(null)]
    public void OpenAIReasoningOptionsBindAndValidate(string? effort)
    {
        using var host = CreateHost("OpenAIChatGPT", "Development", effort);
        Assert.Equal(effort, host.Services.GetRequiredService<IOptions<ModelGatewayOptions>>().Value.ReasoningEffort);
    }

    [Theory]
    [InlineData("OpenAI", "invalid")]
    [InlineData("OpenAIChatGPT", "invalid")]
    [InlineData("Anthropic", "low")]
    public void InvalidOrInapplicableReasoningOptionsFailAtStartup(string mode, string effort)
    {
        using var host = CreateHost(mode, "Development", effort);
        Assert.Throws<OptionsValidationException>(() => host.Services.GetRequiredService<IOptions<ModelGatewayOptions>>().Value);
    }

    private static IHost CreateHost(string mode, string environment, string? effort = null)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ModelGateway:Mode"] = mode,
            ["ModelGateway:Model"] = "test-model",
            ["ModelGateway:ReasoningEffort"] = effort,
            ["ModelGateway:Resilience:Enabled"] = "false",
            ["Storage:AutoInitialize"] = "false",
            ["Storage:Provider"] = "Sqlite",
            ["ConnectionStrings:iRoute"] = "Data Source=:memory:"
        }).Build();
        return new HostBuilder().UseEnvironment(environment).ConfigureServices(services =>
        {
            services.AddLogging();
            services.AddIRoutePlatform(configuration);
        }).Build();
    }
}
