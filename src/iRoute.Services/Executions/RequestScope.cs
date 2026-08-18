using iRoute.Common;

namespace iRoute.Services;

internal static class RequestScope
{
    public static string Tenant(TaskRequest request) =>
        string.IsNullOrWhiteSpace(request.TenantId) ? "local" : request.TenantId.Trim();

    public static string Actor(TaskRequest request) =>
        string.IsNullOrWhiteSpace(request.ActorId) ? "local" : request.ActorId.Trim();
}
