using System.Globalization;
using System.Threading.RateLimiting;
using iRoute.Common;

namespace iRoute.Runtime.Api;

internal static class ApiSafety
{
    public static void AddIRouteRequestLimits(this WebApplicationBuilder builder, IRouteIdentityOptions identity)
    {
        var limits = builder.Configuration.GetSection("ApiLimits").Get<ApiRequestLimits>() ?? new();
        limits.EnsureValid();
        builder.WebHost.ConfigureKestrel(server => server.Limits.MaxRequestBodySize = limits.MaxBodyBytes);
        builder.Services.AddSingleton(limits);
        builder.Services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(context =>
            {
                if (!context.Request.Path.StartsWithSegments("/v1") && context.Request.Path != "/health/model-gateway")
                    return RateLimitPartition.GetNoLimiter("health-static");
                // Only verified JWT claims create tenant partitions. Forged development/anonymous headers
                // cannot create unbounded limiter keys or escape their shared development/anonymous quota.
                var tenant = identity.UsesJwt && context.User.Identity?.IsAuthenticated == true
                    ? context.User.FindFirst(identity.TenantClaim)?.Value : null;
                return RateLimitPartition.GetFixedWindowLimiter(tenant is { Length: > 0 and <= 200 } ? "tenant:" + tenant : "anonymous",
                    _ => new FixedWindowRateLimiterOptions
                    {
                        PermitLimit = limits.RequestsPerMinute,
                        Window = TimeSpan.FromMinutes(1),
                        QueueLimit = 0,
                        AutoReplenishment = true
                    });
            });
            options.OnRejected = async (context, token) =>
            {
                var seconds = context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retry) ? Math.Max(1, Math.Ceiling(retry.TotalSeconds)) : 60;
                context.HttpContext.Response.Headers.RetryAfter = seconds.ToString(CultureInfo.InvariantCulture);
                await Results.Problem(statusCode: StatusCodes.Status429TooManyRequests, title: "Request rate limit exceeded")
                    .ExecuteAsync(context.HttpContext);
            };
        });
    }

    public static void UseIRouteBodyLimit(this WebApplication app)
    {
        var limits = app.Services.GetRequiredService<ApiRequestLimits>();
        app.Use(async (context, next) =>
        {
            if (context.Request.ContentLength > limits.MaxBodyBytes)
            {
                await Results.Problem(statusCode: StatusCodes.Status413PayloadTooLarge, title: "Request body limit exceeded")
                    .ExecuteAsync(context);
                return;
            }
            await next(context);
        });
    }
}
