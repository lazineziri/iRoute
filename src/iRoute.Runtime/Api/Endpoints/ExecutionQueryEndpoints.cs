using System.Globalization;
using System.Text.Json;
using iRoute.Common;
using iRoute.Core;
using Microsoft.Extensions.Options;
using static iRoute.Common.ExecutionStatusFacts;
using static iRoute.Runtime.Api.ExecutionEndpointSupport;

namespace iRoute.Runtime.Api;

internal static class ExecutionQueryEndpoints
{
    internal static async Task<IResult> GetAsync(
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

    internal static async Task<IResult> CancelAsync(
        Guid executionId, HttpRequest request, IOptions<IRouteIdentityOptions> identityOptions,
        ExecutionOrchestrator orchestrator, CancellationToken cancellationToken)
    {
        var identity = RequestIdentity.Resolve(request, identityOptions.Value);
        try
        {
            var result = await orchestrator.CancelAsync(executionId, identity.TenantId, cancellationToken);
            return result is null ? Results.NotFound() : Results.Accepted($"/v1/executions/{executionId}");
        }
        catch (ExecutionCommandException exception)
        {
            return CommandProblem(exception);
        }
    }

    internal static async Task StreamEventsAsync(
        Guid executionId,
        long? after,
        HttpRequest request,
        IOptions<IRouteIdentityOptions> identityOptions,
        IExecutionStore store,
        TimeProvider clock,
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
        var terminalPollsWithoutEvent = 0;
        var lastWriteAt = clock.GetUtcNow();

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
                lastWriteAt = clock.GetUtcNow();
            }

            snapshot = await store.GetAsync(executionId, cancellationToken);
            if (!wroteEvent && snapshot is not null && IsTerminal(snapshot.Status))
            {
                // Terminal state and its final event are separate durable writes. Give the event
                // writer several polls to commit before closing the stream.
                terminalPollsWithoutEvent++;
                if (terminalPollsWithoutEvent >= 4)
                {
                    break;
                }
            }
            else
            {
                terminalPollsWithoutEvent = 0;
            }

            if (!wroteEvent && clock.GetUtcNow() - lastWriteAt >= TimeSpan.FromSeconds(15))
            {
                await response.WriteAsync(": keep-alive\n\n", cancellationToken);
                await response.Body.FlushAsync(cancellationToken);
                lastWriteAt = clock.GetUtcNow();
            }

            await Task.Delay(TimeSpan.FromMilliseconds(250), clock, cancellationToken);
        }
    }

}
