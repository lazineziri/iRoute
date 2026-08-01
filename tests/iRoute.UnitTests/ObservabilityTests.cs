using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Text.Json;
using iRoute.Contracts;
using iRoute.Core;
using iRoute.Infrastructure;
using iRoute.Runtime;
using Microsoft.EntityFrameworkCore;

namespace iRoute.UnitTests;

public sealed class ObservabilityTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 1, 14, 0, 0, TimeSpan.Zero);
    private static readonly string[] PermissionScopes = ["email:send"];
    private static readonly string[] SafeValues = ["first", "second"];

    [Fact]
    public void PayloadExportFailsClosedByDefault()
    {
        Assert.Equal(ObservabilityPayloadMode.MetadataOnly, new ObservabilityOptions().PayloadMode);
    }

    [Fact]
    public async Task SummaryComparesQualityCostLatencyAndMemoryHitsByTaskAndPolicy()
    {
        var executions = new InMemoryExecutionStore();
        await SeedAsync(executions, TestContext.Current.CancellationToken);
        var store = new InMemoryObservabilityStore(executions, Options());

        var summary = await store.QueryAsync(
            new ObservabilityQuery("tenant-a", Now.AddHours(-1), Now.AddHours(1)),
            TestContext.Current.CancellationToken);

        Assert.Equal(3, summary.Totals.Executions);
        Assert.Equal(2, summary.Totals.Completed);
        Assert.Equal(1, summary.Totals.Failed);
        Assert.Equal(2m / 3m, summary.Totals.CompletionRate);
        Assert.Equal(0.875m, summary.Totals.AverageQuality);
        Assert.Equal(100, summary.Totals.InputTokens);
        Assert.Equal(25, summary.Totals.OutputTokens);
        Assert.Equal(0.02m, summary.Totals.Cost);
        Assert.Equal(1, summary.Totals.ModelCalls);
        Assert.Equal(1, summary.Totals.NoModelResolutions);
        Assert.Equal(1, summary.Totals.ModelCallsAvoided);
        Assert.Collection(
            summary.Groups,
            resolution =>
            {
                Assert.Equal("resolution.v1", resolution.PolicyVersion);
                Assert.Equal(1, resolution.Metrics.NoModelResolutions);
            },
            routing =>
            {
                Assert.Equal("routing.w08.v1", routing.PolicyVersion);
                Assert.Equal(0.02m, routing.Metrics.Cost);
            },
            unrouted =>
            {
                Assert.Equal("unrouted", unrouted.PolicyVersion);
                Assert.Equal(1, unrouted.Metrics.Failed);
            });
        Assert.Equal(2, summary.Memory.Considered);
        Assert.Equal(1, summary.Memory.Accepted);
        Assert.Equal(0.5m, summary.Memory.HitRate);
        Assert.Equal(3, summary.RecentExecutions.Count);
    }

    [Fact]
    public async Task TimelineIsTenantScopedOrderedBoundedAndRedacted()
    {
        var executions = new InMemoryExecutionStore();
        var ids = await SeedAsync(executions, TestContext.Current.CancellationToken);
        await executions.AppendEventAsync(
            ids.Generated,
            "privacy.probe",
            Now.AddSeconds(9),
            JsonSerializer.SerializeToElement(new
            {
                input = new { secret = "do-not-export" },
                actorId = "founder@example.test",
                requiredPermissionScopes = PermissionScopes,
                api_key = "do-not-export",
                safeCount = 3,
                inputTokens = 17,
                safeValues = SafeValues,
                message = new string('x', 40)
            }),
            TestContext.Current.CancellationToken);
        var options = Options() with { MaxTimelineEvents = 50, MaxEventValueCharacters = 12 };
        var store = new InMemoryObservabilityStore(executions, options);

        var timeline = await store.GetTimelineAsync(
            "tenant-a",
            ids.Generated,
            TestContext.Current.CancellationToken);

        Assert.NotNull(timeline);
        Assert.Equal("0123456789abcdef0123456789abcdef", timeline.TraceId);
        Assert.StartsWith("sha256:", timeline.ActorReference, StringComparison.Ordinal);
        Assert.DoesNotContain("founder", timeline.ActorReference, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(timeline.Events.OrderBy(item => item.Sequence), timeline.Events);
        var privacy = Assert.Single(timeline.Events, item => item.Type == "privacy.probe");
        Assert.Equal("[REDACTED]", privacy.Data.GetProperty("input").GetString());
        Assert.Equal("[REDACTED]", privacy.Data.GetProperty("actorId").GetString());
        Assert.Equal("[REDACTED]", privacy.Data.GetProperty("requiredPermissionScopes").GetString());
        Assert.Equal("[REDACTED]", privacy.Data.GetProperty("api_key").GetString());
        Assert.Equal(3, privacy.Data.GetProperty("safeCount").GetInt32());
        Assert.Equal(17, privacy.Data.GetProperty("inputTokens").GetInt32());
        Assert.Equal(2, privacy.Data.GetProperty("safeValues").GetArrayLength());
        Assert.EndsWith("…[TRUNCATED]", privacy.Data.GetProperty("message").GetString(), StringComparison.Ordinal);
        Assert.Null(await store.GetTimelineAsync(
            "tenant-b",
            ids.Generated,
            TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task MetadataOnlyModeNeverReturnsEventFields()
    {
        var executions = new InMemoryExecutionStore();
        var ids = await SeedAsync(executions, TestContext.Current.CancellationToken);
        var store = new InMemoryObservabilityStore(
            executions,
            Options() with { PayloadMode = ObservabilityPayloadMode.MetadataOnly });

        var timeline = await store.GetTimelineAsync(
            "tenant-a",
            ids.Generated,
            TestContext.Current.CancellationToken);

        Assert.NotNull(timeline);
        Assert.All(timeline.Events, item => Assert.True(item.Data.GetProperty("redacted").GetBoolean()));
    }

    [Fact]
    public async Task SqliteObservabilitySurvivesStoreReconstruction()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"iroute-observability-{Guid.NewGuid():N}.db");
        try
        {
            var factory = new SqliteContextFactory(databasePath);
            await using (var context = factory.CreateDbContext())
            {
                await context.Database.MigrateAsync(TestContext.Current.CancellationToken);
            }

            var ids = await SeedAsync(
                new EfExecutionStore(factory),
                TestContext.Current.CancellationToken);
            var restarted = new EfObservabilityStore(factory, Options());

            var summary = await restarted.QueryAsync(
                new ObservabilityQuery(
                    "tenant-a",
                    Now.AddHours(-1),
                    Now.AddHours(1),
                    PolicyVersion: "routing.w08.v1"),
                TestContext.Current.CancellationToken);
            var timeline = await restarted.GetTimelineAsync(
                "tenant-a",
                ids.Generated,
                TestContext.Current.CancellationToken);

            Assert.Equal(1, summary.SampledExecutions);
            Assert.Equal(0.02m, summary.Totals.Cost);
            Assert.NotNull(timeline);
            Assert.Contains(timeline.Events, item => item.Type == ExecutionEventTypes.Completed);
        }
        finally
        {
            File.Delete(databasePath);
        }
    }

    [Fact]
    public void RuntimeTelemetryEmitsCorrelatedPayloadFreeSpansAndMetrics()
    {
        var activities = new List<Activity>();
        using var activityListener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == RuntimeTelemetry.ActivitySourceName,
            Sample = static (ref ActivityCreationOptions<ActivityContext> _) =>
                ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = activities.Add
        };
        ActivitySource.AddActivityListener(activityListener);
        var measurements = new List<(string Name, double Value)>();
        using var meterListener = new MeterListener
        {
            InstrumentPublished = (instrument, listener) =>
            {
                if (instrument.Meter.Name == RuntimeTelemetry.MeterName)
                {
                    listener.EnableMeasurementEvents(instrument);
                }
            }
        };
        meterListener.SetMeasurementEventCallback<long>((instrument, measurement, _, _) =>
            measurements.Add((instrument.Name, measurement)));
        meterListener.SetMeasurementEventCallback<double>((instrument, measurement, _, _) =>
            measurements.Add((instrument.Name, measurement)));
        meterListener.Start();
        using var telemetry = new RuntimeTelemetry();
        var snapshot = GeneratedSnapshot(Guid.CreateVersion7());

        using (var trace = telemetry.StartExecution(
                   snapshot,
                   ["project:read", "email:draft"],
                   "execute"))
        {
            Assert.Equal(32, trace.TraceId.Length);
            telemetry.RecordEvent(ExecutionEventTypes.RoutingDecided);
            telemetry.RecordTerminal(snapshot);
        }

        var activity = Assert.Single(activities);
        Assert.Contains(activity.Events, item => item.Name == ExecutionEventTypes.RoutingDecided);
        var tags = activity.TagObjects.ToDictionary(item => item.Key, item => item.Value);
        Assert.StartsWith("sha256:", Assert.IsType<string>(tags["iroute.tenant.ref"]), StringComparison.Ordinal);
        Assert.DoesNotContain("tenant-a", tags.Values.Select(item => item?.ToString()));
        Assert.DoesNotContain("founder@example.test", tags.Values.Select(item => item?.ToString()));
        Assert.DoesNotContain("project:read", tags.Values.Select(item => item?.ToString()));
        Assert.Contains(measurements, item => item.Name == "iroute.executions.started" && item.Value == 1);
        Assert.Contains(measurements, item => item.Name == "iroute.executions.completed" && item.Value == 1);
        Assert.Contains(measurements, item => item.Name == "iroute.execution.quality" && item.Value == 0.8);
        Assert.Contains(measurements, item => item.Name == "iroute.execution.cost" && item.Value == 0.02);
    }

    private static async Task<(Guid Generated, Guid Reused, Guid Failed)> SeedAsync(
        IExecutionStore store,
        CancellationToken cancellationToken)
    {
        var generated = GeneratedSnapshot(Guid.CreateVersion7());
        var reused = ReusedSnapshot(Guid.CreateVersion7());
        var failed = new ExecutionSnapshot(
            Guid.CreateVersion7(),
            "email.draft",
            ExecutionStatus.Failed,
            Now.AddMinutes(-3),
            Now.AddMinutes(-3).AddMilliseconds(40),
            Error: new Problem("test_failure", "Test failure", "Synthetic failure."),
            TenantId: "tenant-a",
            ActorId: "founder@example.test",
            ProjectId: "project-1",
            TaskDefinitionVersion: 1);
        foreach (var snapshot in new[] { generated, reused, failed })
        {
            await store.CreateAsync(snapshot, null, cancellationToken);
            await store.AppendEventAsync(
                snapshot.ExecutionId,
                ExecutionEventTypes.Created,
                snapshot.CreatedAt,
                JsonSerializer.SerializeToElement(new
                {
                    snapshot.TaskType,
                    snapshot.TenantId,
                    snapshot.ActorId,
                    snapshot.ProjectId,
                    traceId = snapshot.ExecutionId == generated.ExecutionId
                        ? "0123456789abcdef0123456789abcdef"
                        : snapshot.ExecutionId.ToString("N")
                }),
                cancellationToken);
        }

        await store.AppendEventAsync(
            generated.ExecutionId,
            ExecutionEventTypes.RoutingDecided,
            generated.CreatedAt.AddMilliseconds(10),
            JsonSerializer.SerializeToElement(new { policyVersion = "routing.w08.v1" }),
            cancellationToken);
        await store.AppendEventAsync(
            generated.ExecutionId,
            ExecutionEventTypes.ResolutionConsidered,
            generated.CreatedAt.AddMilliseconds(5),
            JsonSerializer.SerializeToElement(new
            {
                resolver = "exact-cache",
                accepted = false,
                code = "exact_cache_miss"
            }),
            cancellationToken);
        await store.AppendEventAsync(
            generated.ExecutionId,
            ExecutionEventTypes.Completed,
            generated.UpdatedAt,
            JsonSerializer.SerializeToElement(new { generated.Status }),
            cancellationToken);
        await store.AppendEventAsync(
            reused.ExecutionId,
            ExecutionEventTypes.ResolutionConsidered,
            reused.CreatedAt.AddMilliseconds(5),
            JsonSerializer.SerializeToElement(new
            {
                resolver = "exact-cache",
                accepted = true,
                code = "exact_cache_hit"
            }),
            cancellationToken);
        await store.AppendEventAsync(
            reused.ExecutionId,
            ExecutionEventTypes.Completed,
            reused.UpdatedAt,
            JsonSerializer.SerializeToElement(new { reused.Status }),
            cancellationToken);
        await store.AppendEventAsync(
            failed.ExecutionId,
            ExecutionEventTypes.Failed,
            failed.UpdatedAt,
            JsonSerializer.SerializeToElement(new { failed.Status }),
            cancellationToken);
        return (generated.ExecutionId, reused.ExecutionId, failed.ExecutionId);
    }

    private static ExecutionSnapshot GeneratedSnapshot(Guid id) => new(
        id,
        "email.draft",
        ExecutionStatus.Succeeded,
        Now.AddMinutes(-5),
        Now.AddMinutes(-5).AddMilliseconds(250),
        GeneratedOutcome(),
        TenantId: "tenant-a",
        ActorId: "founder@example.test",
        ProjectId: "project-1",
        TaskDefinitionVersion: 1);

    private static ExecutionSnapshot ReusedSnapshot(Guid id) => new(
        id,
        "email.draft",
        ExecutionStatus.Succeeded,
        Now.AddMinutes(-4),
        Now.AddMinutes(-4).AddMilliseconds(50),
        new TaskOutcome(
            JsonSerializer.SerializeToElement(new { subject = "Reused" }),
            ResolutionLevel.ExactArtifact,
            0.95m,
            [],
            new UsageSummary(),
            []),
        TenantId: "tenant-a",
        ActorId: "founder@example.test",
        ProjectId: "project-1",
        TaskDefinitionVersion: 1);

    private static TaskOutcome GeneratedOutcome() => new(
        JsonSerializer.SerializeToElement(new { subject = "Generated" }),
        ResolutionLevel.SmallModel,
        0.85m,
        [],
        new UsageSummary(100, 25, 0.02m, 180, 1, 0),
        [],
        Validation: new ValidationSummary(true, 0.8m, ["synthetic"], []),
        Routing: new RoutingDecision(
            "routing.w08.v1",
            RoutingPath.Direct,
            "Measured route.",
            "text.generate",
            "small-v1",
            ModelTier.Small,
            0.8m,
            0.85m,
            0.02m,
            180,
            0.05m,
            0.1m,
            false,
            0,
            false,
            null,
            []));

    private static ObservabilityOptions Options() => new()
    {
        PayloadMode = ObservabilityPayloadMode.Redacted,
        MaxQueryDays = 30,
        MaxExecutions = 100,
        MaxTimelineEvents = 100,
        MaxEventValueCharacters = 1_000,
        MaxRecentExecutions = 25
    };

    private sealed class SqliteContextFactory(string databasePath) : IDbContextFactory<IRouteDbContext>
    {
        private readonly DbContextOptions<IRouteDbContext> _options =
            new DbContextOptionsBuilder<IRouteDbContext>()
                .UseSqlite($"Data Source={databasePath}")
                .Options;

        public IRouteDbContext CreateDbContext() => new(_options);
    }
}
