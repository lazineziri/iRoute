using System.Text.Json;
using iRoute.Contracts;
using iRoute.Core;
using iRoute.Runtime;
using Microsoft.Extensions.Options;

namespace iRoute.Worker;

public sealed record ExecutionWorkerOptions
{
    public string? WorkerId { get; init; }
    public TimeSpan PollInterval { get; init; } = TimeSpan.FromMilliseconds(250);
    public TimeSpan LeaseDuration { get; init; } = TimeSpan.FromSeconds(30);
    public TimeSpan HeartbeatInterval { get; init; } = TimeSpan.FromSeconds(5);
    public TimeSpan AbandonDelay { get; init; } = TimeSpan.FromSeconds(1);

    public void EnsureValid()
    {
        if (PollInterval <= TimeSpan.Zero ||
            LeaseDuration <= TimeSpan.Zero ||
            HeartbeatInterval <= TimeSpan.Zero ||
            AbandonDelay < TimeSpan.Zero)
        {
            throw new InvalidOperationException("Execution worker intervals must be positive.");
        }

        if (HeartbeatInterval >= LeaseDuration / 2)
        {
            throw new InvalidOperationException(
                "ExecutionWorker:HeartbeatInterval must be less than half of LeaseDuration.");
        }
    }
}

public sealed partial class ExecutionWorker(
    IServiceScopeFactory scopes,
    IExecutionWorkStore work,
    IExecutionStore executions,
    IClock clock,
    IOptions<ExecutionWorkerOptions> configuredOptions,
    ILogger<ExecutionWorker> logger) : BackgroundService
{
    private readonly ExecutionWorkerOptions _options = configuredOptions.Value;
    private readonly string _workerId = CreateWorkerId(configuredOptions.Value.WorkerId);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _options.EnsureValid();
        LogWorkerStarted(logger, _workerId);
        while (!stoppingToken.IsCancellationRequested)
        {
            ExecutionLease? lease;
            try
            {
                lease = await work.TryClaimAsync(
                    _workerId,
                    clock.UtcNow,
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
        try
        {
            using var scope = scopes.CreateScope();
            var orchestrator = scope.ServiceProvider.GetRequiredService<ExecutionOrchestrator>();
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
                if (!await work.CompleteAsync(lease, clock.UtcNow, CancellationToken.None))
                {
                    LogLeaseLost(logger, lease.ExecutionId, _workerId);
                }
            }
            else
            {
                await AbandonAsync(lease, "non_terminal_result");
            }
        }
        catch (OperationCanceledException)
        {
            await AbandonAsync(
                lease,
                stoppingToken.IsCancellationRequested ? "worker_stopping" : "lease_or_execution_cancelled");
        }
        catch (Exception exception)
        {
            LogExecutionFailed(logger, lease.ExecutionId, exception);
            await AbandonAsync(lease, "worker_failure");
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
            await Task.Delay(_options.HeartbeatInterval, heartbeatCancellation.Token);
            ExecutionLeaseHeartbeat heartbeat;
            try
            {
                heartbeat = await work.RenewAsync(
                    lease,
                    clock.UtcNow,
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

    private async Task AbandonAsync(ExecutionLease lease, string reason)
    {
        var availableAt = clock.UtcNow.Add(_options.AbandonDelay);
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
                clock.UtcNow,
                JsonSerializer.SerializeToElement(data),
                cancellationToken);
        }
        catch (Exception exception)
        {
            LogLeaseEventFailed(logger, executionId, eventType, exception);
        }
    }

    private static async Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(delay, cancellationToken);
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
        var remaining = expiresAt - clock.UtcNow;
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

    [LoggerMessage(15, LogLevel.Error, "Execution {ExecutionId} lease heartbeat failed")]
    private static partial void LogHeartbeatFailed(ILogger logger, Guid executionId, Exception exception);

    [LoggerMessage(16, LogLevel.Warning, "Execution {ExecutionId} lease event {EventType} could not be persisted")]
    private static partial void LogLeaseEventFailed(
        ILogger logger,
        Guid executionId,
        string eventType,
        Exception exception);
}
