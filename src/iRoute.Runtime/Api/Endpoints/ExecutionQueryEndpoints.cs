using System.Globalization;
using System.Text.Json;
using iRoute.Common;
using Microsoft.Extensions.Options;

namespace iRoute.Runtime.Api;

public static partial class ExecutionEndpoints
{
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
        IExecutionWorkStore executionWork,
        IExecutionCancellationRegistry cancellationRegistry,
        TimeProvider clock,
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

        var requestedAt = clock.GetUtcNow();
        if (!await store.TryRequestCancellationAsync(executionId, requestedAt, cancellationToken))
        {
            // The worker reached a terminal state between the read above and this write.
            var settled = await store.GetAsync(executionId, cancellationToken);
            return Problem(
                StatusCodes.Status409Conflict,
                ErrorCodes.ExecutionAlreadyTerminal,
                "Execution already terminal",
                $"Execution '{executionId}' is already {settled?.Status.ToString() ?? "terminal"}.");
        }

        await store.AppendEventAsync(
            executionId,
            ExecutionEventTypes.CancellationRequested,
            requestedAt,
            JsonSerializer.SerializeToElement(new { requestedAt }),
            cancellationToken);
        var problem = new Problem(
            ErrorCodes.ExecutionCancelled,
            "Execution cancelled",
            snapshot.Status == ExecutionStatus.WaitingForApproval
                ? "The execution was cancelled while waiting for approval."
                : "The execution was cancelled before a worker claimed it.");
        var cancelledBeforeLease = snapshot.Status == ExecutionStatus.Queued &&
            await executionWork.CancelPendingAsync(
                executionId,
                requestedAt,
                problem,
                cancellationToken);
        if (snapshot.Status == ExecutionStatus.WaitingForApproval || cancelledBeforeLease)
        {
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
                    from = snapshot.Status,
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
