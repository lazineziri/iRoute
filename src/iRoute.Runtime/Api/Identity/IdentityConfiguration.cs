using System.Security.Claims;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.Options;

namespace iRoute.Runtime.Api;

internal sealed record IRouteIdentityOptions
{
    public const string SectionName = "Identity";
    public const string DevelopmentHeadersMode = "DevelopmentHeaders";
    public const string JwtMode = "Jwt";

    public string Mode { get; init; } = DevelopmentHeadersMode;
    public string? Authority { get; init; }
    public string? Audience { get; init; }
    public bool RequireHttpsMetadata { get; init; } = true;
    public string TenantClaim { get; init; } = "tenant_id";
    public string ActorClaim { get; init; } = "sub";
    public string PermissionClaim { get; init; } = "scope";

    public bool UsesJwt => string.Equals(Mode, JwtMode, StringComparison.OrdinalIgnoreCase);
}

internal sealed record RequestIdentityScope(
    string TenantId,
    string ActorId,
    IReadOnlySet<string> PermissionScopes,
    bool Authenticated);

internal static class IdentityConfiguration
{
    public const string RuntimePolicy = "iroute-runtime";

    public static IRouteIdentityOptions AddIRouteIdentity(
        this IServiceCollection services,
        IConfiguration configuration,
        string environmentName)
    {
        var options = configuration
            .GetSection(IRouteIdentityOptions.SectionName)
            .Get<IRouteIdentityOptions>() ?? new();
        var validator = new IRouteIdentityOptionsValidator(environmentName);
        var validation = validator.Validate(Options.DefaultName, options);
        if (validation.Failed)
        {
            throw new OptionsValidationException(
                Options.DefaultName,
                typeof(IRouteIdentityOptions),
                validation.Failures);
        }

        services.AddSingleton<IValidateOptions<IRouteIdentityOptions>>(validator);
        services.AddOptions<IRouteIdentityOptions>()
            .Bind(configuration.GetSection(IRouteIdentityOptions.SectionName))
            .ValidateOnStart();
        services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
            .AddJwtBearer(jwt =>
            {
                jwt.Authority = options.Authority;
                jwt.Audience = options.Audience;
                jwt.RequireHttpsMetadata = options.RequireHttpsMetadata;
                jwt.MapInboundClaims = false;
            });
        services.AddAuthorizationBuilder()
            .AddPolicy(RuntimePolicy, policy => policy
                .RequireAuthenticatedUser()
                .RequireAssertion(context =>
                    HasNonEmptyClaim(context.User, options.TenantClaim) &&
                    HasNonEmptyClaim(context.User, options.ActorClaim)));
        return options;
    }

    private static bool HasNonEmptyClaim(ClaimsPrincipal principal, string claimType) =>
        principal.Claims.Any(claim =>
            claim.Type == claimType && !string.IsNullOrWhiteSpace(claim.Value));
}

internal sealed class IRouteIdentityOptionsValidator(string environmentName)
    : IValidateOptions<IRouteIdentityOptions>
{
    public ValidateOptionsResult Validate(string? name, IRouteIdentityOptions options)
    {
        if (!string.Equals(
                options.Mode,
                IRouteIdentityOptions.DevelopmentHeadersMode,
                StringComparison.OrdinalIgnoreCase) &&
            !options.UsesJwt)
        {
            return ValidateOptionsResult.Fail(
                $"Unsupported Identity:Mode '{options.Mode}'. Use DevelopmentHeaders or Jwt.");
        }

        if (string.Equals(
                options.Mode,
                IRouteIdentityOptions.DevelopmentHeadersMode,
                StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(environmentName, Environments.Development, StringComparison.OrdinalIgnoreCase))
        {
            return ValidateOptionsResult.Fail(
                "Identity:Mode=DevelopmentHeaders is permitted only when the host environment is Development. " +
                "Configure Identity:Mode=Jwt outside Development.");
        }

        return options.UsesJwt &&
            (string.IsNullOrWhiteSpace(options.Authority) || string.IsNullOrWhiteSpace(options.Audience))
                ? ValidateOptionsResult.Fail(
                    "Identity:Authority and Identity:Audience are required when Identity:Mode is Jwt.")
                : ValidateOptionsResult.Success;
    }
}

internal static class RequestIdentity
{
    public static RequestIdentityScope Resolve(
        HttpRequest request,
        IRouteIdentityOptions options,
        string? requestTenantId = null,
        string? requestActorId = null)
    {
        if (options.UsesJwt)
        {
            var tenantId = ReadClaim(request.HttpContext.User, options.TenantClaim)
                ?? throw new InvalidOperationException("The authenticated identity has no tenant claim.");
            var actorId = ReadClaim(request.HttpContext.User, options.ActorClaim)
                ?? throw new InvalidOperationException("The authenticated identity has no actor claim.");
            return new RequestIdentityScope(
                tenantId,
                actorId,
                ReadScopes(request.HttpContext.User.Claims
                    .Where(claim => claim.Type == options.PermissionClaim)
                    .Select(claim => claim.Value)),
                true);
        }

        return new RequestIdentityScope(
            ReadHeader(request, "X-Tenant-Id") ?? Normalize(requestTenantId) ?? "local",
            ReadHeader(request, "X-Actor-Id") ?? Normalize(requestActorId) ?? "local",
            ReadScopes([request.Headers["X-Permission-Scopes"].ToString()]),
            false);
    }

    public static bool ConflictsWithRequest(
        RequestIdentityScope scope,
        string? tenantId,
        string? actorId) =>
        scope.Authenticated &&
        (HasDifferentValue(tenantId, scope.TenantId) || HasDifferentValue(actorId, scope.ActorId));

    private static string? ReadClaim(ClaimsPrincipal principal, string claimType) =>
        Normalize(principal.Claims.FirstOrDefault(x => x.Type == claimType)?.Value);

    private static string? ReadHeader(HttpRequest request, string name) =>
        Normalize(request.Headers[name].ToString());

    private static bool HasDifferentValue(string? requested, string actual) =>
        Normalize(requested) is { } normalized &&
        !string.Equals(normalized, actual, StringComparison.Ordinal);

    private static HashSet<string> ReadScopes(IEnumerable<string> values) =>
        values
            .SelectMany(value => value.Split([' ', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .ToHashSet(StringComparer.Ordinal);

    private static string? Normalize(string? value)
    {
        var normalized = value?.Trim();
        return string.IsNullOrWhiteSpace(normalized) ? null : normalized;
    }
}
