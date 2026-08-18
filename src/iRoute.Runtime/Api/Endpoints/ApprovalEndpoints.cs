using iRoute.Common;
using iRoute.Core;
using iRoute.Services;
using Microsoft.Extensions.Options;

namespace iRoute.Runtime.Api;

public static partial class ExecutionEndpoints
{
    private static async Task<IResult> SubmitApprovalAsync(
        Guid executionId,
        ApprovalDecision decision,
        HttpRequest request,
        IOptions<IRouteIdentityOptions> identityOptions,
        ExecutionOrchestrator orchestrator,
        CancellationToken cancellationToken)
    {
        var identity = RequestIdentity.Resolve(request, identityOptions.Value);
        try
        {
            var result = await orchestrator.SubmitApprovalForQueueAsync(
                executionId,
                decision,
                identity.TenantId,
                identity.ActorId,
                identity.PermissionScopes,
                cancellationToken);
            return Results.Ok(result);
        }
        catch (ApprovalSubmissionException exception)
        {
            var status = exception.Code switch
            {
                ErrorCodes.InvalidTaskRequest => StatusCodes.Status400BadRequest,
                ErrorCodes.PermissionScopeDenied => StatusCodes.Status403Forbidden,
                ErrorCodes.ApprovalNotFound => StatusCodes.Status404NotFound,
                ErrorCodes.ApprovalAlreadyDecided => StatusCodes.Status409Conflict,
                _ => StatusCodes.Status409Conflict
            };
            return Problem(status, exception.Code, exception.Title, exception.Message);
        }
    }

}
