using System.Text.Json;
using iRoute.Contracts;
using iRoute.Core;
using iRoute.Runtime;
using Microsoft.Extensions.Options;

namespace iRoute.Worker;

internal sealed partial class ExecutionWorker(
    IServiceScopeFactory scopes,
    IExecutionWorkStore work,
    IExecutionStore executions,
    IExecutionFence fence,
    TimeProvider clock,
    IOptions<ExecutionWorkerOptions> configuredOptions,
    ILogger<ExecutionWorker> logger) : BackgroundService
{
    private readonly ExecutionWorkerOptions _options = configuredOptions.Value;
    private readonly string _workerId = CreateWorkerId(configuredOptions.Value.WorkerId);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        LogWorkerStarted(logger, _workerId);
        while (!stoppingToken.IsCancellationRequested)
        {
            ExecutionLease? lease;
            try
            {
                lease = await work.TryClaimAsync(
                    _workerId,
                    clock.GetUtcNow(),
                    _options.LeaseDuration,
                    stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                LogClaimFailed(logger, exception);
                await DelayAsync(_options.PollInterval, stoppingToken);
                continue;
            }

            if (lease is null)
            {
                await DelayAsync(_options.PollInterval, stoppingToken);
                continue;
            }

            await ProcessAsync(lease, stoppingToken);
        }
    }

    private async Task ProcessAsync(ExecutionLease lease, CancellationToken stoppingToken)
    {
        LogLeaseClaimed(logger, lease.ExecutionId, lease.DeliveryAttempt, _workerId);
        await TryAppendEventAsync(
            lease.ExecutionId,
            ExecutionEventTypes.LeaseClaimed,
            new
            {
                workerId = _workerId,
                lease.DeliveryAttempt,
                lease.ExpiresAt
            },
            CancellationToken.None);
        using var processCancellation = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        var heartbeat = MaintainLeaseAsync(lease, processCancellation, stoppingToken);
        using var fenced = fence.Hold(lease.LeaseToken);
        try
        {
            using var scope = scopes.CreateScope();
            var orchestrator = scope.ServiceProvider.GetRequiredService<ExecutionOrchestrator>();
            // Every durable write made while processing this execution is checked against the
            // lease token, so a worker whose lease was taken over cannot interleave writes with
            // the new owner even if its own cancellation has not fired yet.
            var snapshot = await orchestrator.ProcessQueuedAsync(
                lease.ExecutionId,
                processCancellation.Token);
            if (IsTerminal(snapshot.Status))
            {
                await TryAppendEventAsync(
                    lease.ExecutionId,
                    ExecutionEventTypes.LeaseReleased,
                    new { workerId = _workerId, reason = "completed", snapshot.Status },
                    CancellationToken.None);
                if (!await work.CompleteAsync(lease, clock.GetUtcNow(), CancellationToken.None))
                {
                    LogLeaseLost(logger, lease.ExecutionId, _workerId);
                }
            }
            else
            {
                await AbandonAsync(lease, "non_terminal_result");
            }
        }
        catch (OperationCanceledException) when (processCancellation.IsCancellationRequested)
        {
            await AbandonAsync(
                lease,
                stoppingToken.IsCancellationRequested ? "worker_stopping" : "lease_or_execution_cancelled");
        }
        catch (OperationCanceledException exception)
        {
            // A cancellation-shaped exception without cancellation of the worker/lease token is
            // an execution failure. Count it like any other poison delivery.
            LogExecutionFailed(logger, lease.ExecutionId, exception);
            if (_options.HasExhaustedDeliveries(lease.DeliveryAttempt))
            {
                await DeadLetterAsync(lease, exception);
            }
            else
            {
                await AbandonAsync(lease, "worker_failure");
            }
        }
        catch (LeaseFencedException)
        {
            // A successor already owns this execution. Do not dead-letter or append cleanup from
            // the stale worker; its token will also make AbandonAsync a harmless no-op.
            LogLeaseLost(logger, lease.ExecutionId, _workerId);
        }
        catch (Exception exception)
        {
            LogExecutionFailed(logger, lease.ExecutionId, exception);
            if (_options.HasExhaustedDeliveries(lease.DeliveryAttempt))
            {
                // Redelivering a deterministically failing execution forever spins the queue and
                // grows the event log without bound. Settle it instead.
                await DeadLetterAsync(lease, exception);
            }
            else
            {
                await AbandonAsync(lease, "worker_failure");
            }
        }
        finally
        {
            await processCancellation.CancelAsync();
            try
            {
                await heartbeat;
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception exception)
            {
                LogHeartbeatFailed(logger, lease.ExecutionId, exception);
            }
        }
    }

    private async Task MaintainLeaseAsync(
        ExecutionLease lease,
        CancellationTokenSource processCancellation,
        CancellationToken stoppingToken)
    {
        using var heartbeatCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            processCancellation.Token,
            stoppingToken);
        ArmLeaseExpiry(processCancellation, lease.ExpiresAt);
        while (!heartbeatCancellation.IsCancellationRequested)
        {
            await Task.Delay(_options.HeartbeatInterval, clock, heartbeatCancellation.Token);
            ExecutionLeaseHeartbeat heartbeat;
            try
            {
                heartbeat = await work.RenewAsync(
                    lease,
                    clock.GetUtcNow(),
                    _options.LeaseDuration,
                    heartbeatCancellation.Token);
            }
            catch (OperationCanceledException) when (heartbeatCancellation.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                LogHeartbeatFailed(logger, lease.ExecutionId, exception);
                await processCancellation.CancelAsync();
                return;
            }
            if (!heartbeat.Renewed)
            {
                LogLeaseLost(logger, lease.ExecutionId, _workerId);
                await processCancellation.CancelAsync();
                return;
            }

            if (heartbeat.ExpiresAt is { } expiresAt)
            {
                ArmLeaseExpiry(processCancellation, expiresAt);
            }

            await TryAppendEventAsync(
                lease.ExecutionId,
                ExecutionEventTypes.LeaseRenewed,
                new
                {
                    workerId = _workerId,
                    heartbeat.ExpiresAt,
                    heartbeat.CancellationRequested
                },
                CancellationToken.None);
            if (heartbeat.CancellationRequested)
            {
                await processCancellation.CancelAsync();
                return;
            }
        }
    }

    /// <summary>
    /// Fails an execution that has exhausted its deliveries and completes the work item, so the
    /// queue stops re-serving it. The lease token still fences both writes.
    /// </summary>
    private async Task DeadLetterAsync(ExecutionLease lease, Exception exception)
    {
        var now = clock.GetUtcNow();
        var problem = new Problem(
            ErrorCodes.ExecutionFailed,
            "Execution delivery attempts exhausted",
            $"Execution '{lease.ExecutionId}' failed on {lease.DeliveryAttempt} deliveries and was " +
                $"not retried again. Last failure: {exception.Message}",
            Retryable: false);

        LogDeliveriesExhausted(logger, lease.ExecutionId, lease.DeliveryAttempt);

        try
        {
            var snapshot = await executions.GetAsync(lease.ExecutionId, CancellationToken.None);
            if (snapshot is not null && !ExecutionStateMachine.IsTerminal(snapshot.Status) &&
                ExecutionStateMachine.CanTransition(snapshot.Status, ExecutionStatus.Failed))
            {
                await executions.UpdateAsync(
                    snapshot with
                    {
                        Status = ExecutionStatus.Failed,
                        UpdatedAt = now,
                        Error = problem
                    },
                    CancellationToken.None);
                await TryAppendEventAsync(
                    lease.ExecutionId,
                    ExecutionEventTypes.Failed,
                    new { workerId = _workerId, reason = "delivery_attempts_exhausted", problem.Detail },
                    CancellationToken.None);
            }

            await work.CompleteAsync(lease, now, CancellationToken.None);
        }
        catch (Exception deadLetterFailure)
        {
            // Never let the settling path take the worker down; the lease simply expires.
            LogExecutionFailed(logger, lease.ExecutionId, deadLetterFailure);
        }
    }

    private async Task AbandonAsync(ExecutionLease lease, string reason)
    {
        var availableAt = clock.GetUtcNow().Add(_options.AbandonDelayFor(lease.DeliveryAttempt));
        if (await work.AbandonAsync(lease, availableAt, CancellationToken.None))
        {
            await TryAppendEventAsync(
                lease.ExecutionId,
                ExecutionEventTypes.LeaseReleased,
                new { workerId = _workerId, reason, availableAt },
                CancellationToken.None);
        }
    }

    private async Task TryAppendEventAsync(
        Guid executionId,
        string eventType,
        object data,
        CancellationToken cancellationToken)
    {
        try
        {
            await executions.AppendEventAsync(
                executionId,
                eventType,
                clock.GetUtcNow(),
                JsonSerializer.SerializeToElement(data),
                cancellationToken);
        }
        catch (Exception exception)
        {
            LogLeaseEventFailed(logger, executionId, eventType, exception);
        }
    }

    private async Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(delay, clock, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private static string CreateWorkerId(string? configured) =>
        string.IsNullOrWhiteSpace(configured)
            ? $"{Environment.MachineName}:{Environment.ProcessId}:{Guid.CreateVersion7():N}"
            : configured.Trim();

    private void ArmLeaseExpiry(CancellationTokenSource processCancellation, DateTimeOffset expiresAt)
    {
        var remaining = expiresAt - clock.GetUtcNow();
        processCancellation.CancelAfter(remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero);
    }

    private static bool IsTerminal(ExecutionStatus status) => status is
        ExecutionStatus.Succeeded or
        ExecutionStatus.Failed or
        ExecutionStatus.Cancelled or
        ExecutionStatus.TimedOut;

    [LoggerMessage(10, LogLevel.Information, "Durable execution worker {WorkerId} started")]
    private static partial void LogWorkerStarted(ILogger logger, string workerId);

    [LoggerMessage(11, LogLevel.Information, "Execution {ExecutionId} lease attempt {Attempt} claimed by {WorkerId}")]
    private static partial void LogLeaseClaimed(
        ILogger logger,
        Guid executionId,
        int attempt,
        string workerId);

    [LoggerMessage(12, LogLevel.Warning, "Execution {ExecutionId} lease was lost by {WorkerId}")]
    private static partial void LogLeaseLost(ILogger logger, Guid executionId, string workerId);

    [LoggerMessage(13, LogLevel.Error, "Failed to claim durable execution work")]
    private static partial void LogClaimFailed(ILogger logger, Exception exception);

    [LoggerMessage(14, LogLevel.Error, "Durable execution {ExecutionId} failed in the worker host")]
    private static partial void LogExecutionFailed(ILogger logger, Guid executionId, Exception exception);

    [LoggerMessage(
        18,
        LogLevel.Error,
        "Execution {ExecutionId} exhausted {DeliveryAttempt} delivery attempts and was failed")]
    private static partial void LogDeliveriesExhausted(
        ILogger logger,
        Guid executionId,
        int deliveryAttempt);

    [LoggerMessage(15, LogLevel.Error, "Execution {ExecutionId} lease heartbeat failed")]
    private static partial void LogHeartbeatFailed(ILogger logger, Guid executionId, Exception exception);

    [LoggerMessage(16, LogLevel.Warning, "Execution {ExecutionId} lease event {EventType} could not be persisted")]
    private static partial void LogLeaseEventFailed(
        ILogger logger,
        Guid executionId,
        string eventType,
        Exception exception);
}
