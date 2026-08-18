using System.Text.Json;
using iRoute.Common;

namespace iRoute.Services;

public sealed class EmailDraftOutcomeValidator : ITaskOutcomeValidator
{
    public bool Supports(string taskType) => string.Equals(taskType, "email.draft", StringComparison.OrdinalIgnoreCase);

    public Task<OutcomeValidationResult> ValidateAsync(
        TaskRequest request,
        TaskDefinition definition,
        ModelGatewayResult result,
        CompiledContext context,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var checks = new List<string>();
        var failures = new List<string>();

        if (result.Output.ValueKind != JsonValueKind.Object)
        {
            failures.Add("The email draft output must be a JSON object.");
        }
        else
        {
            RequireNonEmptyString(result.Output, "subject", checks, failures);
            RequireNonEmptyString(result.Output, "body", checks, failures);
        }

        var qualityFloor = Math.Max(
            request.Constraints?.MinimumQuality ?? definition.MinimumQuality,
            definition.MinimumQuality);
        if (result.Confidence >= qualityFloor)
        {
            checks.Add("The measured confidence meets the task quality floor.");
        }
        else
        {
            failures.Add($"Measured confidence {result.Confidence:0.###} is below the quality floor {qualityFloor:0.###}.");
        }

        var evidenceRequired = request.Constraints?.RequireEvidence is true || definition.RequiresEvidence;
        if (!evidenceRequired || result.Evidence.Count > 0 || context.Evidence.Count > 0)
        {
            checks.Add("The evidence requirement is satisfied.");
        }
        else
        {
            failures.Add("The task requires evidence, but no evidence was returned or compiled.");
        }

        var quality = failures.Count == 0 ? result.Confidence : Math.Min(result.Confidence, 0.49m);
        return Task.FromResult<OutcomeValidationResult>(new(failures.Count == 0, quality, checks, failures));
    }

    private static void RequireNonEmptyString(
        JsonElement output,
        string propertyName,
        List<string> checks,
        List<string> failures)
    {
        if (output.TryGetProperty(propertyName, out var value) &&
            value.ValueKind == JsonValueKind.String &&
            !string.IsNullOrWhiteSpace(value.GetString()))
        {
            checks.Add($"The '{propertyName}' field is present and non-empty.");
            return;
        }

        failures.Add($"The email draft requires a non-empty '{propertyName}' field.");
    }
}

public sealed class EmailSendOutcomeValidator : ITaskOutcomeValidator
{
    public bool Supports(string taskType) =>
        string.Equals(taskType, "email.send", StringComparison.OrdinalIgnoreCase);

    public Task<OutcomeValidationResult> ValidateAsync(
        TaskRequest request,
        TaskDefinition definition,
        ModelGatewayResult result,
        CompiledContext context,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var failures = new List<string>();
        var checks = new List<string>();
        if (result.Output.ValueKind != JsonValueKind.Object)
        {
            failures.Add("The email action did not return a structured delivery receipt.");
        }
        else
        {
            RequireNonEmptyString(result.Output, "receiptId", checks, failures);
            RequireNonEmptyString(result.Output, "status", checks, failures);
            if (result.Output.TryGetProperty("capability", out var capability) &&
                capability.ValueKind == JsonValueKind.String &&
                string.Equals(capability.GetString(), definition.Capability, StringComparison.Ordinal))
            {
                checks.Add("The delivery receipt confirms the requested capability.");
            }
            else
            {
                failures.Add("The delivery receipt does not confirm the requested capability.");
            }
        }

        if (result.Evidence.Count > 0)
        {
            checks.Add("The external action returned receipt evidence.");
        }
        else
        {
            failures.Add("The external action returned no receipt evidence.");
        }

        return Task.FromResult<OutcomeValidationResult>(new(
            failures.Count == 0,
            failures.Count == 0 ? 1m : 0m,
            checks,
            failures));
    }

    private static void RequireNonEmptyString(
        JsonElement output,
        string propertyName,
        List<string> checks,
        List<string> failures)
    {
        if (output.TryGetProperty(propertyName, out var value) &&
            value.ValueKind == JsonValueKind.String &&
            !string.IsNullOrWhiteSpace(value.GetString()))
        {
            checks.Add($"The delivery receipt contains '{propertyName}'.");
            return;
        }

        failures.Add($"The delivery receipt requires a non-empty '{propertyName}'.");
    }
}

public sealed class DefaultTaskOutcomeValidator : ITaskOutcomeValidator
{
    public bool Supports(string taskType) => true;

    public Task<OutcomeValidationResult> ValidateAsync(
        TaskRequest request,
        TaskDefinition definition,
        ModelGatewayResult result,
        CompiledContext context,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var failures = new List<string>();
        var checks = new List<string>();
        var qualityFloor = Math.Max(
            request.Constraints?.MinimumQuality ?? definition.MinimumQuality,
            definition.MinimumQuality);

        if (result.Output.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            failures.Add("The task returned no output.");
        }
        else
        {
            checks.Add("The task returned a structured output value.");
        }

        if (result.Confidence >= qualityFloor)
        {
            checks.Add("The measured confidence meets the task quality floor.");
        }
        else
        {
            failures.Add($"Measured confidence {result.Confidence:0.###} is below the quality floor {qualityFloor:0.###}.");
        }

        var evidenceRequired = request.Constraints?.RequireEvidence is true || definition.RequiresEvidence;
        if (!evidenceRequired || result.Evidence.Count > 0 || context.Evidence.Count > 0)
        {
            checks.Add("The evidence requirement is satisfied.");
        }
        else
        {
            failures.Add("The task requires evidence, but no evidence was returned or compiled.");
        }

        return Task.FromResult<OutcomeValidationResult>(new(
            failures.Count == 0,
            failures.Count == 0 ? result.Confidence : Math.Min(result.Confidence, 0.49m),
            checks,
            failures));
    }
}
