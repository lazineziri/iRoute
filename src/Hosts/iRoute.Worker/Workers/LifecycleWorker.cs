using iRoute.Core;

namespace iRoute.Worker;

internal sealed partial class LifecycleWorker(
    ILifecycleStore lifecycle,
    LifecyclePolicy policy,
    TimeProvider clock,
    ILogger<LifecycleWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        policy.EnsureValid();
        while (!stoppingToken.IsCancellationRequested)
        {
            var startedAt = clock.GetUtcNow();
            LogLifecycleSweepStarted(logger, startedAt);
            try
            {
                var result = await lifecycle.SweepAsync(policy, startedAt, stoppingToken);
                LogLifecycleSweepCompleted(
                    logger,
                    result.ExpiredArtifacts + result.ExpiredMemoryRecords,
                    result.ArchivedArtifacts + result.ArchivedMemoryRecords,
                    result.DeletedArtifacts + result.DeletedMemoryRecords,
                    result.PurgedArchives,
                    result.ProtectedActiveDependencies,
                    result.After.ArtifactCount,
                    result.After.MemoryCount,
                    result.After.ArchiveCount,
                    result.After.DanglingDependencyEdgeCount);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                LogLifecycleSweepFailed(logger, exception);
            }

            await Task.Delay(policy.SweepInterval, clock, stoppingToken);
        }
    }

    [LoggerMessage(1, LogLevel.Information, "Lifecycle sweep started at {Timestamp}")]
    private static partial void LogLifecycleSweepStarted(ILogger logger, DateTimeOffset timestamp);

    [LoggerMessage(
        2,
        LogLevel.Information,
        "Lifecycle sweep completed: expired={Expired}, archived={Archived}, deleted={Deleted}, " +
        "purgedArchives={PurgedArchives}, protected={ProtectedCount}, artifacts={Artifacts}, " +
        "memory={Memory}, archives={Archives}, danglingEdges={DanglingEdges}")]
    private static partial void LogLifecycleSweepCompleted(
        ILogger logger,
        int expired,
        int archived,
        int deleted,
        int purgedArchives,
        int protectedCount,
        int artifacts,
        int memory,
        int archives,
        int danglingEdges);

    [LoggerMessage(3, LogLevel.Error, "Lifecycle sweep failed")]
    private static partial void LogLifecycleSweepFailed(ILogger logger, Exception exception);
}
