using iRoute.Common;

namespace iRoute.Services;

public sealed class TaskPolicyEngine : ITaskPolicyEngine
{
    public const string CurrentPolicyVersion = "w04.v2";
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

        var step = SelectPolicyStep(plan);
        var effectiveSideEffect = plan.Steps.Max(item => item.SideEffectClass);
        if (effectiveSideEffect > definition.SideEffectClass)
        {
            return Denied(
                step.Capability,
                effectiveSideEffect,
                definition.EffectivePermissionScopes,
                ErrorCodes.CapabilityNotAllowed,
                "The plan side-effect class exceeds the trusted task definition.");
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
                effectiveSideEffect,
                requiredScopes,
                missingScopes,
                ErrorCodes.PermissionScopeDenied,
                "The authenticated actor does not have every permission scope required by the task.");
        }

        var writesExternally = effectiveSideEffect >= SideEffectClass.ReversibleWrite;
        if (writesExternally && request.Constraints?.AllowExternalWrites is not true)
        {
            return Denied(
                step.Capability,
                effectiveSideEffect,
                requiredScopes,
                ErrorCodes.ExternalWriteNotAllowed,
                "The request did not explicitly allow external writes.");
        }

        if (writesExternally && string.IsNullOrWhiteSpace(request.IdempotencyKey))
        {
            return Denied(
                step.Capability,
                effectiveSideEffect,
                requiredScopes,
                ErrorCodes.ExternalActionIdempotencyRequired,
                "External writes require a tenant-scoped idempotency key.");
        }

        var requiresApproval = definition.ApprovalRequired ||
            effectiveSideEffect == SideEffectClass.IrreversibleWrite;
        if (!requiresApproval)
        {
            return Allowed(step.Capability, effectiveSideEffect, requiredScopes);
        }

        if (approval is null || approval.Status == ApprovalStatus.Pending)
        {
            return new PolicyEvaluation(
                CurrentPolicyVersion,
                PolicyDecisionKind.ApprovalRequired,
                step.Capability,
                effectiveSideEffect,
                requiredScopes,
                [],
                Reason: "The trusted task policy requires explicit approval before this action executes.");
        }

        if (approval.Status == ApprovalStatus.Approved &&
            (string.IsNullOrWhiteSpace(approval.DecidedByActorId) ||
             string.Equals(approval.RequestedByActorId, approval.DecidedByActorId, StringComparison.Ordinal)))
        {
            return Denied(step.Capability, effectiveSideEffect, requiredScopes,
                ErrorCodes.PermissionScopeDenied, "An actor cannot approve their own external action.");
        }

        return approval.Status == ApprovalStatus.Approved
            ? Allowed(step.Capability, effectiveSideEffect, requiredScopes)
            : Denied(
                step.Capability,
                effectiveSideEffect,
                requiredScopes,
                ErrorCodes.ApprovalDenied,
                "The proposed external action was denied.");
    }

    private static ExecutionPlanStep SelectPolicyStep(ExecutionPlan plan)
    {
        var effectiveSideEffect = plan.Steps.Max(step => step.SideEffectClass);
        return plan.Steps.Last(step => step.SideEffectClass == effectiveSideEffect);
    }

    public PolicyEvaluation EvaluateApproval(
        ApprovalRecord approval,
        string approverActorId,
        IReadOnlyCollection<string> approverPermissionScopes)
    {
        if (string.IsNullOrWhiteSpace(approverActorId) ||
            string.Equals(approval.RequestedByActorId, approverActorId, StringComparison.Ordinal))
        {
            return Denied(approval.Capability, approval.SideEffectClass, approval.RequiredPermissionScopes,
                ErrorCodes.PermissionScopeDenied, "Approval decisions require an actor other than the requester.");
        }

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
