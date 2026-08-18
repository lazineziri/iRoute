using System.Text.Json;
using iRoute.Common;

namespace iRoute.Services;

public sealed partial class ExecutionService
{
    private static CompiledContext EmptyCompiledContext()
    {
        var content = JsonSerializer.SerializeToElement(new Dictionary<string, object?>());
        return new CompiledContext(
            content,
            new ContextManifest(
                0,
                0,
                0,
                0,
                false,
                false,
                [],
                new Dictionary<string, EvidenceReference>(StringComparer.Ordinal)),
            [],
            content);
    }

    private static void EnsureModelBudgetAllows(TaskRequest request, ExecutionPlan plan)
    {
        if (request.Constraints?.MaxModelCalls is 0 &&
            plan.Steps.Any(step => step.Kind == ExecutionStepKind.Model))
        {
            throw new TaskExecutionException(
                ErrorCodes.ModelBudgetExhausted,
                "Model call budget exhausted",
                "The task cannot be resolved without generation and its model-call budget is zero.");
        }
    }

    private static Dictionary<string, ModelStepGatewayBudget> ModelGatewayBudgets(
        ExecutionPlan plan)
    {
        var modelSteps = plan.Steps
            .Where(step => step.Kind == ExecutionStepKind.Model)
            .ToArray();
        if (modelSteps.Length == 0)
        {
            return new Dictionary<string, ModelStepGatewayBudget>(StringComparer.Ordinal);
        }

        var minimumAttempts = plan.Budget.MaxModelCalls / modelSteps.Length;
        var extraAttempts = plan.Budget.MaxModelCalls % modelSteps.Length;
        var costShare = plan.Budget.MaxCost is { } maximumCost
            ? maximumCost / modelSteps.Length
            : (decimal?)null;
        return modelSteps
            .Select((step, index) => new
            {
                step.Id,
                Budget = new ModelStepGatewayBudget(
                    minimumAttempts + (index < extraAttempts ? 1 : 0),
                    costShare)
            })
            .ToDictionary(item => item.Id, item => item.Budget, StringComparer.Ordinal);
    }

    private static ModelGatewayResult AggregateWorkflowResult(
        ExecutionPlan plan,
        IReadOnlyDictionary<string, JsonElement> outputs,
        ModelGatewayResult finalResult)
    {
        var completed = plan.Steps
            .Where(step => outputs.ContainsKey(step.Id))
            .Select(step => new
            {
                Step = step,
                Result = outputs[step.Id].Deserialize<ModelGatewayResult>(ContractJsonOptions)
                    ?? throw new WorkflowStepExecutionException(
                        step.Id,
                        $"The checkpointed result for step '{step.Id}' is invalid.")
            })
            .ToArray();
        var usage = new UsageSummary(
            completed.Sum(item => item.Result.Usage.InputTokens),
            completed.Sum(item => item.Result.Usage.OutputTokens),
            completed.Sum(item => item.Result.Usage.Cost),
            completed.Sum(item => item.Result.Usage.DurationMilliseconds),
            completed.Sum(item => item.Result.Usage.ModelCalls),
            completed.Sum(item => item.Result.Usage.ToolCalls));
        var evidence = completed
            .SelectMany(item => item.Result.Evidence)
            .DistinctBy(item => (item.Kind, item.Reference, item.ContentHash))
            .ToArray();
        var lastModelResult = completed
            .LastOrDefault(item => item.Step.Kind == ExecutionStepKind.Model)
            ?.Result;
        var modelMetadata = finalResult.Usage.ModelCalls > 0 ? finalResult : lastModelResult;

        return finalResult with
        {
            Usage = usage,
            Evidence = evidence,
            GatewayId = finalResult.GatewayId ?? modelMetadata?.GatewayId,
            Transport = modelMetadata?.Transport ?? finalResult.Transport,
            FinishReason = modelMetadata?.FinishReason ?? finalResult.FinishReason,
            Deployment = finalResult.Deployment ?? modelMetadata?.Deployment,
            Resilience = finalResult.Resilience ?? modelMetadata?.Resilience
        };
    }

    private static void EnsureUsageWithinBudget(TaskRequest request, ModelGatewayResult result)
    {
        if (request.Constraints?.MaxCost is { } maxCost && result.Usage.Cost > maxCost)
        {
            throw new TaskExecutionException(
                ErrorCodes.CostBudgetExceeded,
                "Cost budget exceeded",
                $"The model gateway reported a cost of {result.Usage.Cost}, above the task limit of {maxCost}.");
        }

        if (request.Constraints?.MaxModelCalls is { } maxCalls && result.Usage.ModelCalls > maxCalls)
        {
            throw new TaskExecutionException(
                ErrorCodes.ModelCallBudgetExceeded,
                "Model call budget exceeded",
                $"The task used {result.Usage.ModelCalls} model calls, above the task limit of {maxCalls}.");
        }

        if (request.Constraints?.MaxToolCalls is { } maxToolCalls && result.Usage.ToolCalls > maxToolCalls)
        {
            throw new TaskExecutionException(
                ErrorCodes.ExecutionFailed,
                "Tool call budget exceeded",
                $"The task used {result.Usage.ToolCalls} tool calls, above the task limit of {maxToolCalls}.");
        }
    }

    private static void EnsurePlanMatchesDefinition(ExecutionPlan plan, TaskDefinition definition)
    {
        var issues = new List<ExecutionPlanValidationIssue>();
        if (!string.Equals(plan.TaskType, definition.TaskType, StringComparison.Ordinal) ||
            plan.TaskVersion != definition.Version)
        {
            issues.Add(new(
                "task_definition_mismatch",
                "taskType",
                "The plan task identity does not match the resolved task definition."));
        }

        var required = definition.EffectiveRequiredCapabilities;
        if (plan.Steps.Count != required.Count)
        {
            issues.Add(new(
                "unsupported_plan_shape",
                "steps",
                "The plan step count must match the trusted task definition's required capabilities."));
        }
        else
        {
            for (var index = 0; index < plan.Steps.Count; index++)
            {
                var step = plan.Steps[index];
                if (!string.Equals(step.Capability, required[index], StringComparison.Ordinal) ||
                    !definition.EffectiveAllowedCapabilities.Contains(step.Capability, StringComparer.Ordinal))
                {
                    issues.Add(new(
                        "capability_mismatch",
                        $"steps[{index}].capability",
                        "The plan step does not match the trusted required-capability sequence."));
                }

                if (step.SideEffectClass > definition.SideEffectClass)
                {
                    issues.Add(new(
                        "side_effect_mismatch",
                        $"steps[{index}].sideEffectClass",
                        "A plan step cannot exceed the trusted task side-effect class."));
                }
            }
        }

        if (issues.Count > 0)
        {
            throw new InvalidExecutionPlanException(issues);
        }
    }

    private static ResolutionLevel ResolutionLevelFor(RoutingDecision routing) =>
        routing.SelectedModelTier switch
        {
            ModelTier.Small => ResolutionLevel.SmallModel,
            ModelTier.Verifier => ResolutionLevel.VerifiedOrHuman,
            null => ResolutionLevel.DeterministicCapability,
            _ => ResolutionLevel.StrongModel
        };

    private static bool IsTerminal(ExecutionStatus status) => status is
        ExecutionStatus.Succeeded or
        ExecutionStatus.Failed or
        ExecutionStatus.Cancelled or
        ExecutionStatus.TimedOut;

    private static bool IsExecutionFailure(Exception exception) => exception is
        TaskExecutionException or
        ContextCompilationException or
        RoutingException or
        InvalidExecutionPlanException or
        ExternalActionExecutionException or
        CapabilityInvocationException or
        WorkflowStepTimedOutException or
        WorkflowStepExecutionException or
        ModelGatewayException;

    /// <summary>
    /// Answers a replayed idempotency key: the same payload returns the original execution, a
    /// different payload is a client bug and is reported rather than silently answered with an
    /// unrelated execution.
    /// </summary>
    private static ExecutionSnapshot ReplayOrConflict(
        ExecutionSubmission existing,
        string idempotencyKey,
        string? submissionFingerprint)
    {
        // Executions recorded before submission fingerprints existed have none stored; treat those
        // as retries so an upgrade cannot start rejecting keys that used to work.
        if (existing.InputFingerprint is null ||
            submissionFingerprint is null ||
            string.Equals(existing.InputFingerprint, submissionFingerprint, StringComparison.Ordinal))
        {
            return existing.Execution;
        }

        throw new IdempotencyKeyReusedException(idempotencyKey);
    }

    private static void Validate(TaskRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.TaskType))
        {
            throw new ArgumentException("TaskType is required.", nameof(request));
        }

        if (request.Input.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
        {
            throw new ArgumentException("Input is required.", nameof(request));
        }

        if (request.Constraints?.DeadlineMilliseconds is <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(request), "DeadlineMilliseconds must be positive.");
        }

        if (request.Constraints?.MinimumQuality is < 0 or > 1)
        {
            throw new ArgumentOutOfRangeException(nameof(request), "MinimumQuality must be between zero and one.");
        }

        if (request.Constraints?.AllowedRegions is { } regions &&
            (regions.Count > 64 ||
             regions.Any(region => string.IsNullOrWhiteSpace(region) || region.Length > 200) ||
             regions.Distinct(StringComparer.OrdinalIgnoreCase).Count() != regions.Count))
        {
            throw new ArgumentOutOfRangeException(
                nameof(request),
                "AllowedRegions must contain at most 64 unique non-empty region names of 200 characters or fewer.");
        }

        if (request.Constraints?.RequiredResidency is { } residency &&
            (string.IsNullOrWhiteSpace(residency) || residency.Length > 200))
        {
            throw new ArgumentOutOfRangeException(
                nameof(request),
                "RequiredResidency must be non-empty and no longer than 200 characters.");
        }

        if (request.Metadata?.TryGetValue("artifactKey", out var artifactKey) is true &&
            artifactKey.Trim().Length > 200)
        {
            throw new ArgumentOutOfRangeException(nameof(request), "Metadata artifactKey cannot exceed 200 characters.");
        }
    }

    private sealed class TaskExecutionException(
        string code,
        string title,
        string detail,
        bool retryable = false) : Exception(detail)
    {
        public string Code { get; } = code;
        public string Title { get; } = title;
        public bool Retryable { get; } = retryable;
    }

    private sealed record ModelStepGatewayBudget(
        int MaximumAttempts,
        decimal? MaximumCost);
}
