using System.Text.Json;

namespace iRoute.Common;

public sealed record TaskRequest(
    string TaskType,
    JsonElement Input,
    string? ProjectId = null,
    string? IdempotencyKey = null,
    TaskConstraints? Constraints = null,
    IReadOnlyDictionary<string, string>? Metadata = null,
    string? TenantId = null,
    string? ActorId = null,
    IReadOnlyList<string>? PermissionScopes = null);

public sealed record TaskConstraints(
    int? MaxInputTokens = null,
    int? MaxOutputTokens = null,
    decimal? MaxCost = null,
    int? DeadlineMilliseconds = null,
    decimal? MinimumQuality = null,
    bool RequireEvidence = false,
    bool AllowExternalWrites = false,
    int? MaxModelCalls = null,
    int? MaxToolCalls = null,
    int? MaxParallelCalls = null,
    int? MaxTaskDepth = null,
    IReadOnlyList<string>? AllowedRegions = null,
    string? RequiredResidency = null);

public sealed record ExecutionPlan(
    string PlanId,
    int Version,
    string TaskType,
    int TaskVersion,
    IReadOnlyList<ExecutionPlanStep> Steps,
    ExecutionPlanBudget Budget);

public sealed record ExecutionPlanStep(
    string Id,
    ExecutionStepKind Kind,
    string Capability,
    IReadOnlyList<string> DependsOn,
    SideEffectClass SideEffectClass,
    int TimeoutMilliseconds,
    int MaxAttempts = 1,
    string? ProfileId = null);

public sealed record ExecutionPlanBudget(
    int MaxSteps,
    int MaxModelCalls,
    int MaxToolCalls,
    int MaxParallelCalls,
    int MaxTaskDepth,
    int DeadlineMilliseconds,
    int? MaxInputTokens = null,
    int? MaxOutputTokens = null,
    decimal? MaxCost = null);
