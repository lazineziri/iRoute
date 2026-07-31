using System.Globalization;
using System.Text.Json;
using iRoute.Contracts;
using iRoute.Core;
using iRoute.Runtime;
using Microsoft.Extensions.Options;

namespace iRoute.Api;

public static class ExecutionEndpoints
{
    private static readonly JsonSerializerOptions EventJsonOptions = new(JsonSerializerDefaults.Web);

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
            .Produces<ExecutionSnapshot>(StatusCodes.Status200OK)
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
            var result = await orchestrator.ExecuteAsync(scopedRequest, cancellationToken);
            return Results.Ok(result);
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
            var result = await orchestrator.SubmitApprovalAsync(
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

    private static async Task<IResult> GetAsync(
        Guid executionId,
        HttpRequest request,
        IOptions<IRouteIdentityOptions> identityOptions,
        IExecutionStore store,
        CancellationToken cancellationToken)
    {
        var result = await store.GetAsync(executionId, cancellationToken);
        return result is null || !IsVisibleToTenant(result.TenantId, request, identityOptions.Value)
            ? Results.NotFound()
            : Results.Ok(result);
    }

    private static async Task<IResult> CancelAsync(
        Guid executionId,
        HttpRequest request,
        IOptions<IRouteIdentityOptions> identityOptions,
        IExecutionStore store,
        IWorkflowCheckpointStore checkpoints,
        IExecutionCancellationRegistry cancellationRegistry,
        IClock clock,
        CancellationToken cancellationToken)
    {
        var snapshot = await store.GetAsync(executionId, cancellationToken);
        if (snapshot is null || !IsVisibleToTenant(snapshot.TenantId, request, identityOptions.Value))
        {
            return Results.NotFound();
        }

        if (IsTerminal(snapshot.Status))
        {
            return Problem(
                StatusCodes.Status409Conflict,
                ErrorCodes.ExecutionAlreadyTerminal,
                "Execution already terminal",
                $"Execution '{executionId}' is already {snapshot.Status}.");
        }

        var requestedAt = clock.UtcNow;
        snapshot = snapshot with { CancellationRequestedAt = requestedAt, UpdatedAt = requestedAt };
        await store.UpdateAsync(snapshot, cancellationToken);
        await store.AppendEventAsync(
            executionId,
            ExecutionEventTypes.CancellationRequested,
            requestedAt,
            JsonSerializer.SerializeToElement(new { requestedAt }),
            cancellationToken);
        if (snapshot.Status == ExecutionStatus.WaitingForApproval)
        {
            var problem = new Problem(
                ErrorCodes.ExecutionCancelled,
                "Execution cancelled",
                "The execution was cancelled while waiting for approval.");
            await checkpoints.CancelIncompleteStepsAsync(
                executionId,
                problem,
                requestedAt,
                cancellationToken);
            var cancelled = snapshot with
            {
                Status = ExecutionStatus.Cancelled,
                Error = problem,
                UpdatedAt = requestedAt
            };
            await store.UpdateAsync(cancelled, cancellationToken);
            await store.AppendEventAsync(
                executionId,
                ExecutionEventTypes.StatusChanged,
                requestedAt,
                JsonSerializer.SerializeToElement(new
                {
                    from = ExecutionStatus.WaitingForApproval,
                    to = ExecutionStatus.Cancelled
                }),
                cancellationToken);
            await store.AppendEventAsync(
                executionId,
                ExecutionEventTypes.Failed,
                requestedAt,
                JsonSerializer.SerializeToElement(new
                {
                    status = ExecutionStatus.Cancelled,
                    code = problem.Code,
                    title = problem.Title,
                    retryable = problem.Retryable
                }),
                cancellationToken);
            return Results.Accepted($"/v1/executions/{executionId}");
        }

        cancellationRegistry.RequestCancellation(executionId);
        return Results.Accepted($"/v1/executions/{executionId}");
    }

    private static async Task StreamEventsAsync(
        Guid executionId,
        long? after,
        HttpRequest request,
        IOptions<IRouteIdentityOptions> identityOptions,
        IExecutionStore store,
        HttpResponse response,
        CancellationToken cancellationToken)
    {
        var snapshot = await store.GetAsync(executionId, cancellationToken);
        if (snapshot is null || !IsVisibleToTenant(snapshot.TenantId, request, identityOptions.Value))
        {
            response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        var cursor = after ?? ReadLastEventId(request) ?? 0;
        response.ContentType = "text/event-stream";
        response.Headers.CacheControl = "no-cache";
        response.Headers.Append("X-Accel-Buffering", "no");

        while (!cancellationToken.IsCancellationRequested)
        {
            var wroteEvent = false;
            await foreach (var executionEvent in store.ReadEventsAsync(executionId, cursor, cancellationToken))
            {
                wroteEvent = true;
                cursor = executionEvent.Sequence;
                await response.WriteAsync(
                    $"id: {executionEvent.Sequence.ToString(CultureInfo.InvariantCulture)}\n",
                    cancellationToken);
                await response.WriteAsync($"event: {executionEvent.Type}\n", cancellationToken);
                await response.WriteAsync(
                    $"data: {JsonSerializer.Serialize(executionEvent, EventJsonOptions)}\n\n",
                    cancellationToken);
                await response.Body.FlushAsync(cancellationToken);
            }

            snapshot = await store.GetAsync(executionId, cancellationToken);
            if (!wroteEvent && snapshot is not null && IsTerminal(snapshot.Status))
            {
                break;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(250), cancellationToken);
        }
    }

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
