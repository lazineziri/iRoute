using iRoute.Common;
using Microsoft.Extensions.Options;

namespace iRoute.Runtime.Api;

public static class ObservabilityEndpoints
{
    public static IEndpointRouteBuilder MapIRouteObservabilityEndpoints(
        this IEndpointRouteBuilder endpoints,
        bool requireAuthorization)
    {
        var observability = endpoints.MapGroup("/v1/observability").WithTags("Observability");
        if (requireAuthorization)
        {
            observability.RequireAuthorization(IdentityConfiguration.RuntimePolicy);
        }

        observability.MapGet("/summary", GetSummaryAsync)
            .WithName("GetObservabilitySummary")
            .Produces<ObservabilitySummary>()
            .ProducesProblem(StatusCodes.Status400BadRequest);

        observability.MapGet("/executions/{executionId:guid}", GetTimelineAsync)
            .WithName("GetExecutionTimeline")
            .Produces<ExecutionTimeline>()
            .Produces(StatusCodes.Status404NotFound);

        return endpoints;
    }

    private static async Task<IResult> GetSummaryAsync(
        DateTimeOffset? from,
        DateTimeOffset? to,
        string? taskType,
        string? policyVersion,
        HttpRequest request,
        IOptions<IRouteIdentityOptions> identityOptions,
        IObservabilityStore store,
        TimeProvider clock,
        CancellationToken cancellationToken)
    {
        var identity = RequestIdentity.Resolve(request, identityOptions.Value);
        var end = to ?? clock.GetUtcNow();
        var start = from ?? end.Subtract(TimeSpan.FromHours(24));
        try
        {
            var summary = await store.QueryAsync(
                new ObservabilityQuery(
                    identity.TenantId,
                    start,
                    end,
                    Normalize(taskType),
                    Normalize(policyVersion)),
                cancellationToken);
            return Results.Ok(summary);
        }
        catch (ArgumentException exception)
        {
            return Results.Problem(
                statusCode: StatusCodes.Status400BadRequest,
                title: "Invalid observability query",
                detail: exception.Message,
                extensions: new Dictionary<string, object?>
                {
                    ["code"] = ErrorCodes.InvalidTaskRequest
                });
        }
    }

    private static async Task<IResult> GetTimelineAsync(
        Guid executionId,
        HttpRequest request,
        IOptions<IRouteIdentityOptions> identityOptions,
        IObservabilityStore store,
        CancellationToken cancellationToken)
    {
        var identity = RequestIdentity.Resolve(request, identityOptions.Value);
        var timeline = await store.GetTimelineAsync(
            identity.TenantId,
            executionId,
            cancellationToken);
        return timeline is null ? Results.NotFound() : Results.Ok(timeline);
    }

    private static string? Normalize(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
