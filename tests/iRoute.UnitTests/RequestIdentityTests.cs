using System.Security.Claims;
using iRoute.Api;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace iRoute.UnitTests;

public sealed class RequestIdentityTests
{
    [Fact]
    public void DevelopmentModeUsesHeadersBeforeBodyScope()
    {
        var context = new DefaultHttpContext();
        context.Request.Headers["X-Tenant-Id"] = "header-tenant";
        context.Request.Headers["X-Actor-Id"] = "header-actor";

        var scope = RequestIdentity.Resolve(
            context.Request,
            new IRouteIdentityOptions(),
            "body-tenant",
            "body-actor");

        Assert.Equal("header-tenant", scope.TenantId);
        Assert.Equal("header-actor", scope.ActorId);
        Assert.False(scope.Authenticated);
    }

    [Fact]
    public void JwtModeUsesValidatedClaimsAndRejectsBodyScopeConflict()
    {
        var context = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(
                [new Claim("tenant_id", "claim-tenant"), new Claim("sub", "claim-actor")],
                "Bearer"))
        };
        context.Request.Headers["X-Tenant-Id"] = "untrusted-header";
        var options = new IRouteIdentityOptions { Mode = IRouteIdentityOptions.JwtMode };

        var scope = RequestIdentity.Resolve(context.Request, options);

        Assert.Equal("claim-tenant", scope.TenantId);
        Assert.Equal("claim-actor", scope.ActorId);
        Assert.True(scope.Authenticated);
        Assert.True(RequestIdentity.ConflictsWithRequest(scope, "different-tenant", null));
        Assert.False(RequestIdentity.ConflictsWithRequest(scope, "claim-tenant", "claim-actor"));
    }

    [Fact]
    public void JwtModeRequiresAuthorityAndAudienceAtStartup()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Identity:Mode"] = IRouteIdentityOptions.JwtMode
            })
            .Build();
        var services = new ServiceCollection();

        var exception = Assert.Throws<InvalidOperationException>(() =>
            services.AddIRouteIdentity(configuration));

        Assert.Contains("Identity:Authority", exception.Message, StringComparison.Ordinal);
    }
}
