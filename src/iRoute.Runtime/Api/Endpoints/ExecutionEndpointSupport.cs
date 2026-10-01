using System.Globalization;
using System.Text.Json;
using iRoute.Common;

namespace iRoute.Runtime.Api;

internal static class ExecutionEndpointSupport
{
    internal static readonly JsonSerializerOptions EventJsonOptions = CreateEventJsonOptions();

    internal static JsonSerializerOptions CreateEventJsonOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        options.TypeInfoResolverChain.Add(IRouteApiJsonContext.Default);
        return options;
    }

    internal static IResult Problem(int status, string code, string title, string detail) =>
        Results.Problem(
            statusCode: status,
            title: title,
            detail: detail,
            extensions: new Dictionary<string, object?> { ["code"] = code });

    internal static bool IsVisibleToTenant(
        string tenantId,
        HttpRequest request,
        IRouteIdentityOptions identityOptions)
    {
        var identity = RequestIdentity.Resolve(request, identityOptions);
        return string.Equals(tenantId, identity.TenantId, StringComparison.Ordinal);
    }

    internal static long? ReadLastEventId(HttpRequest request) =>
        long.TryParse(ReadHeader(request, "Last-Event-ID"), NumberStyles.None, CultureInfo.InvariantCulture, out var value)
            ? value
            : null;

    internal static string? ReadHeader(HttpRequest request, string name)
    {
        var value = request.Headers[name].ToString().Trim();
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    internal static IResult CommandProblem(ExecutionCommandException exception) =>
        Problem(exception.Code switch
        {
            ErrorCodes.PermissionScopeDenied => StatusCodes.Status403Forbidden,
            ErrorCodes.ExecutionAlreadyTerminal => StatusCodes.Status409Conflict,
            _ => StatusCodes.Status400BadRequest
        }, exception.Code, exception.Title, exception.Message);

}
