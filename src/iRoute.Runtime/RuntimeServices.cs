using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using iRoute.Contracts;
using iRoute.Core;

namespace iRoute.Runtime;

public sealed class DirectExecutionPlanFactory : IExecutionPlanFactory
{
    public ExecutionPlan Create(TaskRequest request, TaskDefinition definition)
    {
        var deadline = Math.Min(
            request.Constraints?.DeadlineMilliseconds ?? definition.DefaultDeadlineMilliseconds,
            definition.DefaultDeadlineMilliseconds);
        var stepKind = definition.SideEffectClass == SideEffectClass.None
            ? ExecutionStepKind.Model
            : ExecutionStepKind.Tool;
        var maxModelCalls = request.Constraints?.MaxModelCalls ?? definition.DefaultMaxModelCalls;
        var maxToolCalls = request.Constraints?.MaxToolCalls ?? (stepKind == ExecutionStepKind.Tool ? 1 : 0);

        return new ExecutionPlan(
            $"{definition.TaskType}@{definition.Version}:direct",
            1,
            definition.TaskType,
            definition.Version,
            [
                new ExecutionPlanStep(
                    "execute",
                    stepKind,
                    definition.Capability,
                    [],
                    definition.SideEffectClass,
                    deadline,
                    1)
            ],
            new ExecutionPlanBudget(
                1,
                maxModelCalls,
                maxToolCalls,
                request.Constraints?.MaxParallelCalls ?? 1,
                request.Constraints?.MaxTaskDepth ?? 1,
                deadline,
                request.Constraints?.MaxInputTokens ?? definition.DefaultMaxInputTokens,
                request.Constraints?.MaxOutputTokens ?? definition.DefaultMaxOutputTokens,
                request.Constraints?.MaxCost));
    }
}

public sealed class Sha256InputFingerprint : IInputFingerprint
{
    public string Create(TaskRequest request, int taskDefinitionVersion)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("taskType", request.TaskType);
            writer.WriteNumber("taskDefinitionVersion", taskDefinitionVersion);
            writer.WritePropertyName("input");
            CanonicalJson.Write(writer, request.Input);
            writer.WriteEndObject();
        }

        return Convert.ToHexStringLower(SHA256.HashData(stream.ToArray()));
    }
}

public sealed class BoundedContextCompiler : IContextCompiler
{
    private static readonly string[] ContextProperties =
        ["activeDecisions", "projectHistory", "authoritativeSources", "preferences", "context"];

    public Task<CompiledContext> CompileAsync(
        TaskRequest request,
        TaskDefinition definition,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var budget = request.Constraints?.MaxInputTokens ?? definition.DefaultMaxInputTokens;
        var entries = new List<ContextManifestEntry>();
        var evidence = new List<EvidenceReference>();
        var selected = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        var usedTokens = 0;
        var truncated = false;

        if (request.Input.ValueKind == JsonValueKind.Object)
        {
            foreach (var propertyName in ContextProperties)
            {
                if (!request.Input.TryGetProperty(propertyName, out var value))
                {
                    continue;
                }

                var estimatedTokens = TokenEstimator.Estimate(value);
                var reference = $"input.{propertyName}";
                var contentHash = CanonicalJson.Hash(value);
                if (usedTokens + estimatedTokens <= budget)
                {
                    selected[propertyName] = value.Clone();
                    usedTokens += estimatedTokens;
                    entries.Add(new ContextManifestEntry(
                        "request",
                        reference,
                        true,
                        "Selected by the task context policy.",
                        estimatedTokens,
                        contentHash));
                    evidence.Add(new EvidenceReference("request", reference, contentHash));
                }
                else
                {
                    truncated = true;
                    entries.Add(new ContextManifestEntry(
                        "request",
                        reference,
                        false,
                        "Excluded because the context budget was reached.",
                        estimatedTokens,
                        contentHash));
                }
            }
        }

        var content = JsonSerializer.SerializeToElement(selected);
        var manifest = new ContextManifest(usedTokens, budget, truncated, entries);
        return Task.FromResult(new CompiledContext(content, manifest, evidence));
    }
}

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

        var qualityFloor = request.Constraints?.MinimumQuality ?? definition.MinimumQuality;
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
        var qualityFloor = request.Constraints?.MinimumQuality ?? definition.MinimumQuality;

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

public sealed class ExecutionCancellationRegistry : IExecutionCancellationRegistry, IDisposable
{
    private readonly ConcurrentDictionary<Guid, CancellationTokenSource> _sources = new();

    public CancellationToken Register(Guid executionId, CancellationToken requestCancellation)
    {
        var source = CancellationTokenSource.CreateLinkedTokenSource(requestCancellation);
        if (!_sources.TryAdd(executionId, source))
        {
            source.Dispose();
            throw new InvalidOperationException("Execution cancellation is already registered.");
        }

        return source.Token;
    }

    public bool RequestCancellation(Guid executionId)
    {
        if (!_sources.TryGetValue(executionId, out var source))
        {
            return false;
        }

        source.Cancel();
        return true;
    }

    public void Complete(Guid executionId)
    {
        if (_sources.TryRemove(executionId, out var source))
        {
            source.Dispose();
        }
    }

    public void Dispose()
    {
        foreach (var source in _sources.Values)
        {
            source.Dispose();
        }

        _sources.Clear();
    }
}

internal static class RequestScope
{
    public static string Tenant(TaskRequest request) =>
        string.IsNullOrWhiteSpace(request.TenantId) ? "local" : request.TenantId.Trim();

    public static string Actor(TaskRequest request) =>
        string.IsNullOrWhiteSpace(request.ActorId) ? "local" : request.ActorId.Trim();
}

internal static class TokenEstimator
{
    public static int Estimate(JsonElement value)
    {
        var bytes = Encoding.UTF8.GetByteCount(value.GetRawText());
        return Math.Max(1, (int)Math.Ceiling(bytes / 4d));
    }
}

internal static class CanonicalJson
{
    public static string Hash(JsonElement value)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            Write(writer, value);
        }

        return Convert.ToHexStringLower(SHA256.HashData(stream.ToArray()));
    }

    public static void Write(Utf8JsonWriter writer, JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var property in element.EnumerateObject().OrderBy(x => x.Name, StringComparer.Ordinal))
                {
                    writer.WritePropertyName(property.Name);
                    Write(writer, property.Value);
                }

                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in element.EnumerateArray())
                {
                    Write(writer, item);
                }

                writer.WriteEndArray();
                break;
            default:
                element.WriteTo(writer);
                break;
        }
    }
}
