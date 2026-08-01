using System.Collections.Concurrent;
using System.Text.Json;
using iRoute.Contracts;
using iRoute.Core;
using iRoute.Infrastructure;
using iRoute.Runtime;
using Microsoft.EntityFrameworkCore;

namespace iRoute.UnitTests;

public sealed class BoundedDependencySchedulerTests
{
    [Fact]
    public async Task DependencyExecutionHonorsParallelAndQueueBounds()
    {
        var executions = new InMemoryExecutionStore();
        var checkpoints = new InMemoryWorkflowCheckpointStore();
        using var scheduler = CreateScheduler(checkpoints, executions, queueCapacity: 1, maxParallelSteps: 2);
        var executionId = Guid.CreateVersion7();
        var plan = CreatePlan(
            [
                Step("root-a"),
                Step("root-b"),
                Step("root-c"),
                Step("root-d"),
                Step("join", "root-a", "root-b", "root-c", "root-d"),
                Step("finish", "join")
            ],
            maxParallelCalls: 2,
            maxDepth: 3);
        var completed = new ConcurrentDictionary<string, byte>(StringComparer.Ordinal);
        var concurrent = 0;
        var maximumConcurrent = 0;

        var result = await scheduler.ExecuteAsync(
            executionId,
            CreateRequest(),
            plan,
            Routing(plan),
            async (step, dependencies, cancellationToken) =>
            {
                var current = Interlocked.Increment(ref concurrent);
                UpdateMaximum(ref maximumConcurrent, current);
                try
                {
                    Assert.Equal(step.DependsOn.Count, dependencies.Count);
                    Assert.All(step.DependsOn, dependency => Assert.True(completed.ContainsKey(dependency)));
                    await Task.Delay(25, cancellationToken);
                    completed[step.Id] = 0;
                    return JsonSerializer.SerializeToElement(new { step = step.Id });
                }
                finally
                {
                    Interlocked.Decrement(ref concurrent);
                }
            },
            TestContext.Current.CancellationToken);

        Assert.Equal(6, result.Outputs.Count);
        Assert.InRange(maximumConcurrent, 1, 2);
        Assert.InRange(result.PeakQueuedSteps, 0, 1);
        Assert.True(result.BackpressureWaitCount > 0);
        var checkpoint = await checkpoints.GetAsync(executionId, TestContext.Current.CancellationToken);
        Assert.All(Assert.IsType<WorkflowCheckpoint>(checkpoint).Steps, step =>
            Assert.Equal(WorkflowStepStatus.Succeeded, step.Status));
    }

    [Fact]
    public async Task CancellationStopsRunningAndDownstreamSteps()
    {
        var executions = new InMemoryExecutionStore();
        var checkpoints = new InMemoryWorkflowCheckpointStore();
        using var scheduler = CreateScheduler(checkpoints, executions);
        var executionId = Guid.CreateVersion7();
        var plan = CreatePlan([Step("root"), Step("downstream", "root")]);
        var rootStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var downstreamInvocations = 0;
        using var cancellation = new CancellationTokenSource();

        var run = scheduler.ExecuteAsync(
            executionId,
            CreateRequest(),
            plan,
            Routing(plan),
            async (step, _, cancellationToken) =>
            {
                if (step.Id == "downstream")
                {
                    Interlocked.Increment(ref downstreamInvocations);
                    return JsonSerializer.SerializeToElement(new { step = step.Id });
                }

                rootStarted.SetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                return JsonSerializer.SerializeToElement(new { step = step.Id });
            },
            cancellation.Token);
        await rootStarted.Task.WaitAsync(TestContext.Current.CancellationToken);

        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
        Assert.Equal(0, downstreamInvocations);
        var checkpoint = Assert.IsType<WorkflowCheckpoint>(
            await checkpoints.GetAsync(executionId, TestContext.Current.CancellationToken));
        Assert.All(checkpoint.Steps, step => Assert.Equal(WorkflowStepStatus.Cancelled, step.Status));
    }

    [Fact]
    public async Task StepTimeoutIsCheckpointedAndStopsDependents()
    {
        var executions = new InMemoryExecutionStore();
        var checkpoints = new InMemoryWorkflowCheckpointStore();
        using var scheduler = CreateScheduler(checkpoints, executions);
        var executionId = Guid.CreateVersion7();
        var plan = CreatePlan(
            [
                Step("slow") with { TimeoutMilliseconds = 100 },
                Step("downstream", "slow")
            ]);
        var downstreamInvocations = 0;

        var exception = await Assert.ThrowsAsync<WorkflowStepTimedOutException>(() =>
            scheduler.ExecuteAsync(
                executionId,
                CreateRequest(),
                plan,
                Routing(plan),
                async (step, _, cancellationToken) =>
                {
                    if (step.Id == "downstream")
                    {
                        Interlocked.Increment(ref downstreamInvocations);
                        return JsonSerializer.SerializeToElement(new { step = step.Id });
                    }

                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                    return JsonSerializer.SerializeToElement(new { step = step.Id });
                },
                TestContext.Current.CancellationToken));

        Assert.Equal("slow", exception.StepId);
        Assert.Equal(0, downstreamInvocations);
        var checkpoint = Assert.IsType<WorkflowCheckpoint>(
            await checkpoints.GetAsync(executionId, TestContext.Current.CancellationToken));
        Assert.Equal(
            WorkflowStepStatus.TimedOut,
            checkpoint.Steps.Single(step => step.StepId == "slow").Status);
        Assert.Equal(
            WorkflowStepStatus.Cancelled,
            checkpoint.Steps.Single(step => step.StepId == "downstream").Status);
    }

    [Fact]
    public async Task RetryableFailureUsesBoundedBackoffAndRetryAfter()
    {
        var executions = new InMemoryExecutionStore();
        var checkpoints = new InMemoryWorkflowCheckpointStore();
        using var scheduler = new BoundedDependencyScheduler(
            checkpoints,
            executions,
            new SystemClock(),
            new WorkflowSchedulerOptions
            {
                RetryBaseDelayMilliseconds = 1,
                RetryMaxDelayMilliseconds = 50,
                RetryJitterRatio = 0
            });
        var executionId = Guid.CreateVersion7();
        var attempts = 0;
        var plan = CreatePlan([Step("retry") with { MaxAttempts = 3 }]);

        var result = await scheduler.ExecuteAsync(
            executionId,
            CreateRequest(),
            plan,
            Routing(plan),
            (_, _, _) =>
            {
                var attempt = Interlocked.Increment(ref attempts);
                if (attempt < 3)
                {
                    throw new ModelGatewayException(
                        ErrorCodes.ModelGatewayHttpError,
                        "Temporarily unavailable.",
                        true,
                        429,
                        failureKind: ModelGatewayFailureKind.RateLimited,
                        retryAfter: TimeSpan.FromMilliseconds(25));
                }

                return Task.FromResult(JsonSerializer.SerializeToElement(new { value = "recovered" }));
            },
            TestContext.Current.CancellationToken);

        Assert.Equal(3, attempts);
        Assert.Equal("recovered", result.Outputs["retry"].GetProperty("value").GetString());
        var events = await ReadEventsAsync(executions, executionId);
        var retries = events.Where(item => item.Type == ExecutionEventTypes.StepRetryScheduled).ToArray();
        Assert.Equal(2, retries.Length);
        Assert.All(retries, item =>
            Assert.Equal(25, item.Data.GetProperty("delayMilliseconds").GetInt32()));
    }

    [Fact]
    public async Task NonRetryableFailureDoesNotConsumeAdditionalAttempts()
    {
        var executions = new InMemoryExecutionStore();
        var checkpoints = new InMemoryWorkflowCheckpointStore();
        using var scheduler = CreateScheduler(checkpoints, executions);
        var executionId = Guid.CreateVersion7();
        var attempts = 0;
        var plan = CreatePlan([Step("invalid") with { MaxAttempts = 3 }]);

        await Assert.ThrowsAsync<ModelGatewayException>(() => scheduler.ExecuteAsync(
            executionId,
            CreateRequest(),
            plan,
            Routing(plan),
            (_, _, _) =>
            {
                Interlocked.Increment(ref attempts);
                throw new ModelGatewayException(
                    ErrorCodes.ModelGatewayInvalidResponse,
                    "Invalid provider response.",
                    false,
                    failureKind: ModelGatewayFailureKind.InvalidResponse);
            },
            TestContext.Current.CancellationToken));

        Assert.Equal(1, attempts);
        var events = await ReadEventsAsync(executions, executionId);
        Assert.DoesNotContain(events, item => item.Type == ExecutionEventTypes.StepRetryScheduled);
    }

    [Fact]
    public async Task SqliteRestartResumesInterruptedStepWithoutRepeatingCompletedStep()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"iroute-workflow-{Guid.NewGuid():N}.db");
        try
        {
            var factory = new SqliteContextFactory(databasePath);
            await using (var context = factory.CreateDbContext())
            {
                await context.Database.MigrateAsync(TestContext.Current.CancellationToken);
                var applied = await context.Database.GetAppliedMigrationsAsync(TestContext.Current.CancellationToken);
                Assert.Contains(iRoute.Infrastructure.Migrations.WorkflowCheckpoints.MigrationId, applied);
            }

            var executionId = Guid.CreateVersion7();
            var now = DateTimeOffset.UtcNow;
            var executionStore = new EfExecutionStore(factory);
            await executionStore.CreateAsync(
                new ExecutionSnapshot(
                    executionId,
                    "test.workflow",
                    ExecutionStatus.Running,
                    now,
                    now,
                    TenantId: "tenant-a",
                    ActorId: "test-runner"),
                null,
                TestContext.Current.CancellationToken);
            var request = CreateRequest();
            var plan = CreatePlan([Step("completed"), Step("interrupted", "completed")]);
            var beforeCrash = new EfWorkflowCheckpointStore(factory);
            await beforeCrash.InitializeAsync(
                executionId,
                request,
                plan,
                Routing(plan),
                now,
                TestContext.Current.CancellationToken);
            await beforeCrash.StartStepAsync(
                executionId,
                "completed",
                now,
                TestContext.Current.CancellationToken);
            await beforeCrash.CompleteStepAsync(
                executionId,
                "completed",
                JsonSerializer.SerializeToElement(new { value = "durable" }),
                now.AddMilliseconds(1),
                TestContext.Current.CancellationToken);
            await beforeCrash.StartStepAsync(
                executionId,
                "interrupted",
                now.AddMilliseconds(2),
                TestContext.Current.CancellationToken);

            var completedInvocations = 0;
            var interruptedInvocations = 0;
            using var resumedScheduler = CreateScheduler(
                new EfWorkflowCheckpointStore(factory),
                executionStore);
            var result = await resumedScheduler.ResumeAsync(
                executionId,
                (step, dependencies, _) =>
                {
                    if (step.Id == "completed")
                    {
                        Interlocked.Increment(ref completedInvocations);
                    }
                    else
                    {
                        Interlocked.Increment(ref interruptedInvocations);
                        Assert.Equal("durable", dependencies["completed"].GetProperty("value").GetString());
                    }

                    return Task.FromResult(JsonSerializer.SerializeToElement(new { step = step.Id }));
                },
                TestContext.Current.CancellationToken);

            Assert.Equal(0, completedInvocations);
            Assert.Equal(1, interruptedInvocations);
            Assert.Equal(1, result.RecoveredStepCount);
            var afterRestart = Assert.IsType<WorkflowCheckpoint>(
                await new EfWorkflowCheckpointStore(factory).GetAsync(
                    executionId,
                    TestContext.Current.CancellationToken));
            Assert.Equal("test.v1", afterRestart.Routing.PolicyVersion);
            Assert.Equal(RoutingPath.Workflow, afterRestart.Routing.Path);
            Assert.All(afterRestart.Steps, step => Assert.Equal(WorkflowStepStatus.Succeeded, step.Status));
        }
        finally
        {
            File.Delete(databasePath);
        }
    }

    private static BoundedDependencyScheduler CreateScheduler(
        IWorkflowCheckpointStore checkpoints,
        IExecutionStore executions,
        int queueCapacity = 16,
        int maxParallelSteps = 4) =>
        new(
            checkpoints,
            executions,
            new SystemClock(),
            new WorkflowSchedulerOptions
            {
                QueueCapacity = queueCapacity,
                MaxParallelSteps = maxParallelSteps
            });

    private static TaskRequest CreateRequest() => new(
        "test.workflow",
        JsonSerializer.SerializeToElement(new { value = "input" }),
        TenantId: "tenant-a",
        ActorId: "test-runner");

    private static ExecutionPlan CreatePlan(
        IReadOnlyList<ExecutionPlanStep> steps,
        int maxParallelCalls = 4,
        int maxDepth = 4) =>
        new(
            "test.workflow@1",
            1,
            "test.workflow",
            1,
            steps,
            new ExecutionPlanBudget(
                steps.Count,
                0,
                0,
                maxParallelCalls,
                maxDepth,
                30_000));

    private static ExecutionPlanStep Step(string id, params string[] dependencies) =>
        new(
            id,
            ExecutionStepKind.Deterministic,
            $"test.{id}",
            dependencies,
            SideEffectClass.None,
            5_000);

    private static RoutingDecision Routing(ExecutionPlan plan) => new(
        "test.v1",
        RoutingPath.Workflow,
        "Test workflow route.",
        plan.Steps[^1].Capability,
        null,
        null,
        1m,
        1m,
        0m,
        1,
        0m,
        1m,
        true,
        1,
        false,
        null,
        []);

    private static async Task<IReadOnlyList<ExecutionEvent>> ReadEventsAsync(
        InMemoryExecutionStore store,
        Guid executionId)
    {
        var events = new List<ExecutionEvent>();
        await foreach (var item in store.ReadEventsAsync(
                           executionId,
                           0,
                           TestContext.Current.CancellationToken))
        {
            events.Add(item);
        }

        return events;
    }

    private static void UpdateMaximum(ref int target, int candidate)
    {
        var current = Volatile.Read(ref target);
        while (candidate > current)
        {
            var observed = Interlocked.CompareExchange(ref target, candidate, current);
            if (observed == current)
            {
                return;
            }

            current = observed;
        }
    }

    private sealed class SqliteContextFactory(string databasePath) : IDbContextFactory<IRouteDbContext>
    {
        private readonly DbContextOptions<IRouteDbContext> _options =
            new DbContextOptionsBuilder<IRouteDbContext>()
                .UseSqlite($"Data Source={databasePath}")
                .Options;

        public IRouteDbContext CreateDbContext() => new(_options);
    }
}
