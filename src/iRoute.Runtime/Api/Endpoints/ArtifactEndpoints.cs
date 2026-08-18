using System.Globalization;
using iRoute.Common;
using Microsoft.Extensions.Options;

namespace iRoute.Runtime.Api;

public static partial class ExecutionEndpoints
{
    private static async Task<IResult> GetArtifactAsync(
        Guid artifactId,
        HttpRequest request,
        IOptions<IRouteIdentityOptions> identityOptions,
        IArtifactStore store,
        CancellationToken cancellationToken)
    {
        var identity = RequestIdentity.Resolve(request, identityOptions.Value);
        var artifact = await store.GetAsync(identity.TenantId, artifactId, cancellationToken);
        return artifact is null ? Results.NotFound() : Results.Ok(artifact.ToSnapshot());
    }

    private static IResult Problem(int status, string code, string title, string detail) =>
        Results.Problem(
            statusCode: status,
            title: title,
            detail: detail,
            extensions: new Dictionary<string, object?> { ["code"] = code });

    private static bool IsVisibleToTenant(
        string tenantId,
        HttpRequest request,
        IRouteIdentityOptions identityOptions)
    {
        var identity = RequestIdentity.Resolve(request, identityOptions);
        return string.Equals(tenantId, identity.TenantId, StringComparison.Ordinal);
    }

    private static long? ReadLastEventId(HttpRequest request) =>
        long.TryParse(ReadHeader(request, "Last-Event-ID"), NumberStyles.None, CultureInfo.InvariantCulture, out var value)
            ? value
            : null;

    private static string? ReadHeader(HttpRequest request, string name)
    {
        var value = request.Headers[name].ToString().Trim();
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    private static bool IsTerminal(ExecutionStatus status) => status is
        ExecutionStatus.Succeeded or
        ExecutionStatus.Failed or
        ExecutionStatus.Cancelled or
        ExecutionStatus.TimedOut;
}
