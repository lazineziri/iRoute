using iRoute.Common;
using Microsoft.Extensions.Options;

namespace iRoute.Runtime.Api;

internal static class ArtifactEndpoints
{
    internal static async Task<IResult> GetArtifactAsync(
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

}
