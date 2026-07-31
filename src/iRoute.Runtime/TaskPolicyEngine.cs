using System.Security.Cryptography;
using System.Text;
using iRoute.Contracts;
using iRoute.Core;

namespace iRoute.Runtime;

public sealed class TaskPolicyEngine : ITaskPolicyEngine
{
    public const string CurrentPolicyVersion = "w04.v1";
    public const string ApprovalPermissionScope = "approval:grant";

    public PolicyEvaluation Evaluate(
        TaskRequest request,
        TaskDefinition definition,
        ExecutionPlan plan,
        ApprovalRecord? approval = null)
    {
        var allowed = definition.EffectiveAllowedCapabilities.ToHashSet(StringComparer.Ordinal);
        var deniedStep = plan.Steps.FirstOrDefault(step => !allowed.Contains(step.Capability));
        if (deniedStep is not null)
        {
            return Denied(
                deniedStep.Capability,
                deniedStep.SideEffectClass,
                definition.EffectivePermissionScopes,
                ErrorCodes.CapabilityNotAllowed,
                $"Capability '{deniedStep.Capability}' is not allow-listed for task '{definition.TaskType}'.");
        }

        var step = plan.Steps[0];
        if (step.SideEffectClass != definition.SideEffectClass)
        {
            return Denied(
                step.Capability,
                step.SideEffectClass,
                definition.EffectivePermissionScopes,
                ErrorCodes.CapabilityNotAllowed,
                "The plan side-effect class does not match the trusted task definition.");
        }

        var grantedScopes = (request.PermissionScopes ?? [])
            .ToHashSet(StringComparer.Ordinal);
        var requiredScopes = definition.EffectivePermissionScopes
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
        var missingScopes = requiredScopes
            .Where(scope => !grantedScopes.Contains(scope))
            .ToArray();
        if (missingScopes.Length > 0)
        {
            return new PolicyEvaluation(
                CurrentPolicyVersion,
                PolicyDecisionKind.Denied,
                step.Capability,
                step.SideEffectClass,
                requiredScopes,
                missingScopes,
                ErrorCodes.PermissionScopeDenied,
                "The authenticated actor does not have every permission scope required by the task.");
        }

        var writesExternally = step.SideEffectClass >= SideEffectClass.ReversibleWrite;
        if (writesExternally && request.Constraints?.AllowExternalWrites is not true)
        {
            return Denied(
                step.Capability,
                step.SideEffectClass,
                requiredScopes,
                ErrorCodes.ExternalWriteNotAllowed,
                "The request did not explicitly allow external writes.");
        }

        if (writesExternally && string.IsNullOrWhiteSpace(request.IdempotencyKey))
        {
            return Denied(
                step.Capability,
                step.SideEffectClass,
                requiredScopes,
                ErrorCodes.ExternalActionIdempotencyRequired,
                "External writes require a tenant-scoped idempotency key.");
        }

        var requiresApproval = definition.ApprovalRequired ||
            step.SideEffectClass == SideEffectClass.IrreversibleWrite;
        if (!requiresApproval)
        {
            return Allowed(step.Capability, step.SideEffectClass, requiredScopes);
        }

        if (approval is null || approval.Status == ApprovalStatus.Pending)
        {
            return new PolicyEvaluation(
                CurrentPolicyVersion,
                PolicyDecisionKind.ApprovalRequired,
                step.Capability,
                step.SideEffectClass,
                requiredScopes,
                [],
                Reason: "The trusted task policy requires explicit approval before this action executes.");
        }

        return approval.Status == ApprovalStatus.Approved
            ? Allowed(step.Capability, step.SideEffectClass, requiredScopes)
            : Denied(
                step.Capability,
                step.SideEffectClass,
                requiredScopes,
                ErrorCodes.ApprovalDenied,
                "The proposed external action was denied.");
    }

    public PolicyEvaluation EvaluateApproval(
        ApprovalRecord approval,
        IReadOnlyCollection<string> approverPermissionScopes)
    {
        var requiredScopes = approval.RequiredPermissionScopes
            .Append(ApprovalPermissionScope)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
        var grantedScopes = approverPermissionScopes.ToHashSet(StringComparer.Ordinal);
        var missingScopes = requiredScopes
            .Where(scope => !grantedScopes.Contains(scope))
            .ToArray();
        return missingScopes.Length == 0
            ? Allowed(approval.Capability, approval.SideEffectClass, requiredScopes)
            : new PolicyEvaluation(
                CurrentPolicyVersion,
                PolicyDecisionKind.Denied,
                approval.Capability,
                approval.SideEffectClass,
                requiredScopes,
                missingScopes,
                ErrorCodes.PermissionScopeDenied,
                "The actor is not authorized to decide this approval.");
    }

    private static PolicyEvaluation Allowed(
        string capability,
        SideEffectClass sideEffectClass,
        IReadOnlyList<string> requiredScopes) =>
        new(
            CurrentPolicyVersion,
            PolicyDecisionKind.Allowed,
            capability,
            sideEffectClass,
            requiredScopes,
            []);

    private static PolicyEvaluation Denied(
        string capability,
        SideEffectClass sideEffectClass,
        IReadOnlyList<string> requiredScopes,
        string code,
        string reason) =>
        new(
            CurrentPolicyVersion,
            PolicyDecisionKind.Denied,
            capability,
            sideEffectClass,
            requiredScopes,
            [],
            code,
            reason);
}

internal static class PolicyReferences
{
    public static string CreateActionIdempotencyReference(
        string tenantId,
        string requestIdempotencyKey,
        string actionId,
        string capability)
    {
        var value = string.Join('\n', tenantId, requestIdempotencyKey, actionId, capability);
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    }
}

public sealed class ApprovalSubmissionException(
    string code,
    string title,
    string message) : Exception(message)
{
    public string Code { get; } = code;
    public string Title { get; } = title;
}

public sealed class ExternalActionExecutionException(
    string code,
    string title,
    string message,
    bool retryable = false,
    Exception? innerException = null) : Exception(message, innerException)
{
    public string Code { get; } = code;
    public string Title { get; } = title;
    public bool Retryable { get; } = retryable;
}
