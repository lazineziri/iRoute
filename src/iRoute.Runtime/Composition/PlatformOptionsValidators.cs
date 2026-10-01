using iRoute.Common;
using iRoute.Data;
using iRoute.Services;
using Microsoft.Extensions.Options;

namespace iRoute.Runtime.Composition;

internal sealed class ModelGatewayOptionsValidator(IHostEnvironment environment) : IValidateOptions<ModelGatewayOptions>
{
    public ValidateOptionsResult Validate(string? name, ModelGatewayOptions options)
    {
        if (!ModelGatewayModes.IsSupported(options.Mode))
        {
            return ValidateOptionsResult.Fail(
                "ModelGateway:Mode must be Deterministic, Http, OpenAI, Anthropic, OpenAIChatGPT, or ClaudeCode.");
        }

        if (!environment.IsDevelopment() && (ModelGatewayModes.IsSubscription(options.Mode) ||
            options.Deployments.Any(route => ModelGatewayModes.IsSubscription(route.Adapter))))
            return ValidateOptionsResult.Fail("Personal subscription routes are local-development only. Use per-tenant API deployments in hosted environments.");

        return OptionsValidation.From(() =>
        {
            options.Resilience.EnsureValid();
            if (!options.Mode.Equals("Deterministic", StringComparison.OrdinalIgnoreCase))
                _ = new ConfiguredGatewayDeploymentRegistry(Options.Create(options));
        });
    }
}

internal sealed class StorageOptionsValidator : IValidateOptions<StorageOptions>
{
    public ValidateOptionsResult Validate(string? name, StorageOptions options) =>
        OptionsValidation.From(() => StorageProvider.Parse(options.Provider));
}

internal sealed class ObservabilityOptionsValidator : IValidateOptions<ObservabilityOptions>
{
    public ValidateOptionsResult Validate(string? name, ObservabilityOptions options) =>
        OptionsValidation.From(options.EnsureValid);
}

internal sealed class LifecyclePolicyValidator : IValidateOptions<LifecyclePolicy>
{
    public ValidateOptionsResult Validate(string? name, LifecyclePolicy options) =>
        OptionsValidation.From(options.EnsureValid);
}

internal static class OptionsValidation
{
    public static ValidateOptionsResult From(Action validation)
    {
        try
        {
            validation();
            return ValidateOptionsResult.Success;
        }
        catch (InvalidOperationException exception)
        {
            return ValidateOptionsResult.Fail(exception.Message);
        }
    }
}
