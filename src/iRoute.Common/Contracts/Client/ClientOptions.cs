namespace iRoute.Common;

public sealed record IRouteClientOptions(
    string? TenantId = null,
    string? ActorId = null,
    IReadOnlyCollection<string>? PermissionScopes = null,
    string? BearerToken = null);

public sealed record ObservabilityQueryOptions(
    DateTimeOffset? From = null,
    DateTimeOffset? To = null,
    string? TaskType = null,
    string? PolicyVersion = null);
