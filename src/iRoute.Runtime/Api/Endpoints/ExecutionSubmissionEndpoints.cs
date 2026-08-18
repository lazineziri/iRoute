using System.Text.Json;
using iRoute.Common;
using iRoute.Core;
using Microsoft.Extensions.Options;

namespace iRoute.Runtime.Api;

public static partial class ExecutionEndpoints
{
    private static readonly JsonSerializerOptions EventJsonOptions = CreateEventJsonOptions();

    private static JsonSerializerOptions CreateEventJsonOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        options.TypeInfoResolverChain.Add(IRouteApiJsonContext.Default);
        return options;
    }

    public static IEndpointRouteBuilder MapIRouteEndpoints(
        this IEndpointRouteBuilder endpoints,
        bool requireAuthorization)
    {
        var executions = endpoints.MapGroup("/v1/executions").WithTags("Executions");
        var artifacts = endpoints.MapGroup("/v1/artifacts").WithTags("Artifacts");
        if (requireAuthorization)
        {
            executions.RequireAuthorization(IdentityConfiguration.RuntimePolicy);
            artifacts.RequireAuthorization(IdentityConfiguration.RuntimePolicy);
        }

        executions.MapPost("/", ExecuteAsync)
            .WithName("CreateExecution")
            .Produces<ExecutionSnapshot>(StatusCodes.Status202Accepted)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status409Conflict);

        executions.MapGet("/{executionId:guid}", GetAsync)
            .WithName("GetExecution")
            .Produces<ExecutionSnapshot>()
            .Produces(StatusCodes.Status404NotFound);

        executions.MapGet("/{executionId:guid}/events", StreamEventsAsync)
            .WithName("StreamExecutionEvents")
            .Produces(StatusCodes.Status404NotFound);

        executions.MapPost("/{executionId:guid}/cancel", CancelAsync)
            .WithName("CancelExecution")
            .Produces(StatusCodes.Status202Accepted)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);

        executions.MapPost("/{executionId:guid}/approvals", SubmitApprovalAsync)
            .WithName("SubmitExecutionApproval")
            .Produces<ApprovalResult>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);

        executions.MapGet("/{executionId:guid}/external-actions", ListUnresolvedActionsAsync)
            .WithName("ListUnresolvedExternalActions")
            .Produces<IReadOnlyList<UnresolvedExternalAction>>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound);

        executions.MapPost("/{executionId:guid}/external-actions/{actionId}/reconcile", ReconcileActionAsync)
            .WithName("ReconcileExternalAction")
            .Produces<UnresolvedExternalAction>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound);

        artifacts.MapGet("/{artifactId:guid}", GetArtifactAsync)
            .WithName("GetArtifact")
            .Produces<ArtifactSnapshot>()
            .Produces(StatusCodes.Status404NotFound);

        return endpoints;
    }

    private static async Task<IResult> ExecuteAsync(
        TaskRequest request,
        HttpRequest httpRequest,
        IOptions<IRouteIdentityOptions> identityOptions,
        ExecutionOrchestrator orchestrator,
        CancellationToken cancellationToken)
    {
        var headerIdempotencyKey = ReadHeader(httpRequest, "Idempotency-Key");
        if (!string.IsNullOrWhiteSpace(headerIdempotencyKey) &&
            !string.IsNullOrWhiteSpace(request.IdempotencyKey) &&
            !string.Equals(headerIdempotencyKey, request.IdempotencyKey, StringComparison.Ordinal))
        {
            return Problem(
                StatusCodes.Status400BadRequest,
                ErrorCodes.IdempotencyKeyConflict,
                "Idempotency key conflict",
                "The request body and Idempotency-Key header contain different values.");
        }

        var scope = RequestIdentity.Resolve(
            httpRequest,
            identityOptions.Value,
            request.TenantId,
            request.ActorId);
        if (RequestIdentity.ConflictsWithRequest(scope, request.TenantId, request.ActorId))
        {
            return Problem(
                StatusCodes.Status403Forbidden,
                ErrorCodes.IdentityScopeConflict,
                "Identity scope conflict",
                "The request tenant or actor does not match the authenticated identity.");
        }

        var scopedRequest = request with
        {
            TenantId = scope.TenantId,
            ActorId = scope.ActorId,
            PermissionScopes = scope.PermissionScopes.Order(StringComparer.Ordinal).ToArray(),
            IdempotencyKey = headerIdempotencyKey ?? request.IdempotencyKey
        };
        try
        {
            var result = await orchestrator.SubmitAsync(scopedRequest, cancellationToken);
            return Results.Accepted($"/v1/executions/{result.ExecutionId}", result);
        }
        catch (IdempotencyKeyReusedException exception)
        {
            return Problem(
                StatusCodes.Status409Conflict,
                ErrorCodes.IdempotencyKeyConflict,
                "Idempotency key conflict",
                exception.Message);
        }
        catch (ArgumentException exception)
        {
            return Problem(
                StatusCodes.Status400BadRequest,
                ErrorCodes.InvalidTaskRequest,
                "Invalid task request",
                exception.Message);
        }
    }

}
