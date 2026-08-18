namespace iRoute.Common;

public sealed record ResolutionConsideration(
    string Resolver,
    bool Accepted,
    string Code,
    string Reason,
    bool PermissionChecked,
    bool FreshnessChecked,
    int Checks,
    ResolutionLevel? Level = null);

public static class ResolutionDecisionCodes
{
    public const string ExactCacheHit = "exact_cache_hit";
    public const string ExactCacheMiss = "exact_cache_miss";
    public const string PermissionDenied = "permission_denied";
    public const string UnsupportedTask = "unsupported_task";
    public const string ProjectScopeRequired = "project_scope_required";
    public const string StateKeyRequired = "state_key_required";
    public const string StateHit = "state_hit";
    public const string StateUnavailable = "state_unavailable";
    public const string ArtifactReferenceRequired = "artifact_reference_required";
    public const string ArtifactHit = "artifact_hit";
    public const string ArtifactUnavailable = "artifact_unavailable";
    public const string HandlerUnavailable = "handler_unavailable";
    public const string HandlerDeclined = "handler_declined";
    public const string HandlerStale = "handler_stale";
    public const string HandlerAccepted = "handler_accepted";
    public const string ExternalWriteBlocked = "external_write_blocked";
    public const string ValidationFailed = "validation_failed";

    public static IReadOnlySet<string> All { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        ExactCacheHit,
        ExactCacheMiss,
        PermissionDenied,
        UnsupportedTask,
        ProjectScopeRequired,
        StateKeyRequired,
        StateHit,
        StateUnavailable,
        ArtifactReferenceRequired,
        ArtifactHit,
        ArtifactUnavailable,
        HandlerUnavailable,
        HandlerDeclined,
        HandlerStale,
        HandlerAccepted,
        ExternalWriteBlocked,
        ValidationFailed
    };
}
