using iRoute.Common;
using iRoute.Data;
using Microsoft.Extensions.Options;

namespace iRoute.Runtime.Composition;

internal sealed class ModelGatewayOptionsValidator : IValidateOptions<ModelGatewayOptions>
{
    public ValidateOptionsResult Validate(string? name, ModelGatewayOptions options)
    {
        if (!string.Equals(options.Mode, "Deterministic", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(options.Mode, "Http", StringComparison.OrdinalIgnoreCase))
        {
            return ValidateOptionsResult.Fail(
                "ModelGateway:Mode must be either Deterministic or Http.");
        }

        return OptionsValidation.From(options.Resilience.EnsureValid);
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
