using System.Runtime.CompilerServices;
using System.Text.Json;
using iRoute.Contracts;
using iRoute.Core;
using iRoute.Infrastructure;
using iRoute.Runtime;
using Microsoft.EntityFrameworkCore;

namespace iRoute.UnitTests;

[Collection(PostgresIntegrationGroup.Name)]
public sealed class ExecutionOrchestratorTests
{
    private static readonly string[] ActiveDecisions =
        ["Use the Markdown specification as the source of truth."];
    private static readonly string[] ChangedDecisions =
        ["Use PostgreSQL as the production source of truth."];

    [Fact]
    public async Task CreatedEventFailureTerminalizesInsertedExecutionAndItsReplay()
    {
        var inner = new InMemoryExecutionStore();
        var store = new FailCreatedEventOnceExecutionStore(
            inner,
            new InvalidOperationException("Synthetic Created-event write failure."));
        using var cancellations = new ExecutionCancellationRegistry();
        var orchestrator = CreateTestOrchestrator(store, cancellations);
        var request = CreateRequest("tenant-a", "created-event-failure");

        var failed = await orchestrator.ExecuteAsync(
            request,
            TestContext.Current.CancellationToken);
        var replayed = await orchestrator.ExecuteAsync(
            request,
            TestContext.Current.CancellationToken);

        Assert.Equal(ExecutionStatus.Failed, failed.Status);
        Assert.Equal(ErrorCodes.ExecutionFailed, Assert.IsType<Problem>(failed.Error).Code);
        Assert.Equal(failed.ExecutionId, replayed.ExecutionId);
        Assert.Equal(ExecutionStatus.Failed, replayed.Status);
        Assert.Equal(
            ExecutionStatus.Failed,
            Assert.IsType<ExecutionSnapshot>(await inner.GetAsync(
                failed.ExecutionId,
                TestContext.Current.CancellationToken)).Status);
    }

    [Fact]
    public async Task CancellationDuringCreatedEventTerminalizesInsertedExecution()
    {
        var inner = new InMemoryExecutionStore();
        var store = new FailCreatedEventOnceExecutionStore(
            inner,
            new OperationCanceledException("The submitting client disconnected."));
        using var cancellations = new ExecutionCancellationRegistry();
        var orchestrator = CreateTestOrchestrator(store, cancellations);

        var cancelled = await orchestrator.ExecuteAsync(
            CreateRequest("tenant-a", "created-event-cancellation"),
            TestContext.Current.CancellationToken);

        Assert.Equal(ExecutionStatus.Cancelled, cancelled.Status);
        Assert.Equal(ErrorCodes.ExecutionCancelled, Assert.IsType<Problem>(cancelled.Error).Code);
        Assert.Equal(
            ExecutionStatus.Cancelled,
            Assert.IsType<ExecutionSnapshot>(await inner.GetAsync(
                cancelled.ExecutionId,
                TestContext.Current.CancellationToken)).Status);
    }

    [Fact]
    public async Task EmailDraftCompletesWithValidatedArtifactAndOrderedEvents()
    {
        var store = new InMemoryExecutionStore();
        var artifacts = new InMemoryArtifactStore();
        var memories = new InMemoryMemoryStore();
        using var cancellations = new ExecutionCancellationRegistry();
        var orchestrator = CreateOrchestrator(
            store,
            artifacts,
            cancellations,
            memories: memories);

        var result = await orchestrator.ExecuteAsync(CreateRequest("tenant-a", "first"), TestContext.Current.CancellationToken);

        Assert.Equal(ExecutionStatus.Succeeded, result.Status);
        var outcome = Assert.IsType<TaskOutcome>(result.Outcome);
        Assert.Equal(ResolutionLevel.SmallModel, outcome.ResolutionLevel);
        Assert.Equal(1, outcome.Usage.ModelCalls);
        Assert.True(Assert.IsType<ValidationSummary>(outcome.Validation).Passed);
        var context = Assert.IsType<ContextManifest>(outcome.Context);
        Assert.False(context.Truncated);
        Assert.False(context.FullHistoryIncluded);
        Assert.NotEmpty(context.Provenance);
        Assert.Equal(context.EstimatedTokens, outcome.Usage.InputTokens);
        var routing = Assert.IsType<RoutingDecision>(outcome.Routing);
        Assert.Equal(RoutingPath.Direct, routing.Path);
        Assert.Equal("text.generation.small.eval-v1", routing.SelectedProfileId);
        Assert.Equal(ModelTier.Small, routing.SelectedModelTier);
        Assert.False(routing.PlannerInvoked);
        Assert.Equal(0, routing.PlanningCalls);
        Assert.False(routing.Escalated);
        Assert.NotEmpty(routing.Candidates);
        Assert.All(
            context.Entries.Where(entry => entry.Included),
            entry => Assert.True(context.Provenance.ContainsKey(entry.OutputPath!)));
        var artifactReference = Assert.Single(outcome.Artifacts);
        var artifact = await artifacts.GetAsync(
            "tenant-a",
            artifactReference.ArtifactId,
            TestContext.Current.CancellationToken);
        Assert.NotNull(artifact);
        Assert.Equal("email.draft", artifact.ArtifactType);

        var events = await ReadEventsAsync(store, result.ExecutionId);
        Assert.Equal(Enumerable.Range(1, events.Count).Select(x => (long)x), events.Select(x => x.Sequence));
        Assert.Equal(ExecutionEventTypes.Created, events[0].Type);
        Assert.Equal(ExecutionEventTypes.Completed, events[^1].Type);
        Assert.Contains(events, x => x.Type == ExecutionEventTypes.PlanValidated);
        var routingEvent = Assert.Single(events, x => x.Type == ExecutionEventTypes.RoutingDecided);
        Assert.Equal("routing.w18.v1", routingEvent.Data.GetProperty("policyVersion").GetString());
        Assert.Equal("Direct", routingEvent.Data.GetProperty("path").GetString());
        Assert.False(routingEvent.Data.GetProperty("plannerInvoked").GetBoolean());
        Assert.Equal(0, routingEvent.Data.GetProperty("planningCalls").GetInt32());
        Assert.True(routingEvent.Data.GetProperty("candidates").GetArrayLength() >= 2);
        Assert.Contains(events, x => x.Type == ExecutionEventTypes.WorkflowCheckpointed);
        Assert.Contains(events, x => x.Type == ExecutionEventTypes.StepStarted);
        Assert.Contains(events, x => x.Type == ExecutionEventTypes.StepCompleted);
        Assert.Contains(events, x => x.Type == ExecutionEventTypes.ContextCompiled);
        var contextEvent = Assert.Single(events, x => x.Type == ExecutionEventTypes.ContextCompiled);
        Assert.Equal(context.EstimatedTokens, contextEvent.Data.GetProperty("estimatedTokens").GetInt32());
        Assert.Equal(context.BudgetTokens, contextEvent.Data.GetProperty("budgetTokens").GetInt32());
        Assert.Equal(
            context.ProjectedInputTokens,
            contextEvent.Data.GetProperty("projectedInputTokens").GetInt32());
        Assert.Equal(context.ContextTokens, contextEvent.Data.GetProperty("contextTokens").GetInt32());
        Assert.False(contextEvent.Data.GetProperty("fullHistoryIncluded").GetBoolean());
        Assert.Equal(context.Provenance.Count, contextEvent.Data.GetProperty("provenance").GetInt32());
        Assert.Contains(events, x => x.Type == ExecutionEventTypes.ValidationCompleted);
        Assert.Contains(events, x => x.Type == ExecutionEventTypes.MemoryMaterialized);
        Assert.Contains(events, x => x.Type == ExecutionEventTypes.ArtifactMaterialized);
    }

    [Fact]
    public async Task IdenticalInputReusesExactArtifactWithoutModelCall()
    {
        var store = new InMemoryExecutionStore();
        var artifacts = new InMemoryArtifactStore();
        using var cancellations = new ExecutionCancellationRegistry();
        var orchestrator = CreateOrchestrator(store, artifacts, cancellations);

        var first = await orchestrator.ExecuteAsync(CreateRequest("tenant-a", "first"), TestContext.Current.CancellationToken);
        var second = await orchestrator.ExecuteAsync(CreateRequest("tenant-a", "second"), TestContext.Current.CancellationToken);

        var firstOutcome = Assert.IsType<TaskOutcome>(first.Outcome);
        var secondOutcome = Assert.IsType<TaskOutcome>(second.Outcome);
        Assert.Equal(ResolutionLevel.ExactArtifact, secondOutcome.ResolutionLevel);
        Assert.Equal(0, secondOutcome.Usage.ModelCalls);
        Assert.Equal(
            Assert.Single(firstOutcome.Artifacts).ArtifactId,
            Assert.Single(secondOutcome.Artifacts).ArtifactId);
    }

    [Fact]
    public async Task SubmitQueuesModelWorkBeforeAWorkerInvokesTheGateway()
    {
        var store = new InMemoryExecutionStore();
        var artifacts = new InMemoryArtifactStore();
        var gateway = new CountingModelGateway();
        var work = new InMemoryExecutionWorkStore(store);
        using var cancellations = new ExecutionCancellationRegistry();
        var orchestrator = CreateOrchestrator(
            store,
            artifacts,
            cancellations,
            gateway,
            executionWork: work);

        var submitted = await orchestrator.SubmitAsync(
            CreateRequest("tenant-a", "queued-model"),
            TestContext.Current.CancellationToken);

        Assert.Equal(ExecutionStatus.Queued, submitted.Status);
        Assert.Equal(0, gateway.InvocationCount);
        Assert.Equal(
            ExecutionWorkState.Pending,
            Assert.IsType<ExecutionWorkItem>(await work.GetAsync(
                submitted.ExecutionId,
                TestContext.Current.CancellationToken)).State);

        var lease = Assert.IsType<ExecutionLease>(await work.TryClaimAsync(
            "worker-a",
            DateTimeOffset.UtcNow,
            TimeSpan.FromSeconds(30),
            TestContext.Current.CancellationToken));
        var completed = await orchestrator.ProcessQueuedAsync(
            submitted.ExecutionId,
            TestContext.Current.CancellationToken);
        Assert.True(await work.CompleteAsync(
            lease,
            DateTimeOffset.UtcNow,
            TestContext.Current.CancellationToken));

        Assert.Equal(ExecutionStatus.Succeeded, completed.Status);
        Assert.Equal(1, gateway.InvocationCount);
        var events = await ReadEventsAsync(store, submitted.ExecutionId);
        Assert.Contains(events, item => item.Type == ExecutionEventTypes.Queued);
        Assert.Contains(events, item =>
            item.Type == ExecutionEventTypes.StatusChanged &&
            item.Data.GetProperty("to").GetString() == nameof(ExecutionStatus.Running));
    }

    [Fact]
    public async Task SubmitKeepsExactArtifactResolutionOutOfTheWorkerQueue()
    {
        var store = new InMemoryExecutionStore();
        var artifacts = new InMemoryArtifactStore();
        var gateway = new CountingModelGateway();
        var work = new InMemoryExecutionWorkStore(store);
        using var cancellations = new ExecutionCancellationRegistry();
        var orchestrator = CreateOrchestrator(
            store,
            artifacts,
            cancellations,
            gateway,
            executionWork: work);
        await orchestrator.ExecuteAsync(
            CreateRequest("tenant-a", "exact-source"),
            TestContext.Current.CancellationToken);

        var submitted = await orchestrator.SubmitAsync(
            CreateRequest("tenant-a", "exact-fast-path"),
            TestContext.Current.CancellationToken);

        Assert.Equal(ExecutionStatus.Succeeded, submitted.Status);
        Assert.Equal(
            ResolutionLevel.ExactArtifact,
            Assert.IsType<TaskOutcome>(submitted.Outcome).ResolutionLevel);
        Assert.Null(await work.GetAsync(
            submitted.ExecutionId,
            TestContext.Current.CancellationToken));
        Assert.Equal(1, gateway.InvocationCount);
    }

    [Fact]
    public async Task InterruptedWorkerIsTakenOverAndResumesItsRunningCheckpoint()
    {
        var store = new InMemoryExecutionStore();
        var artifacts = new InMemoryArtifactStore();
        var checkpoints = new InMemoryWorkflowCheckpointStore();
        var gateway = new InterruptOnceModelGateway();
        var work = new InMemoryExecutionWorkStore(store);
        using var cancellations = new ExecutionCancellationRegistry();
        var orchestrator = CreateOrchestrator(
            store,
            artifacts,
            cancellations,
            gateway,
            checkpoints: checkpoints,
            executionWork: work);
        var submitted = await orchestrator.SubmitAsync(
            CreateRequest("tenant-a", "worker-takeover"),
            TestContext.Current.CancellationToken);
        var firstLease = Assert.IsType<ExecutionLease>(await work.TryClaimAsync(
            "worker-a",
            DateTimeOffset.UtcNow,
            TimeSpan.FromSeconds(30),
            TestContext.Current.CancellationToken));
        using var interrupted = new CancellationTokenSource();
        var firstAttempt = orchestrator.ProcessQueuedAsync(submitted.ExecutionId, interrupted.Token);
        await gateway.FirstAttemptStarted.Task.WaitAsync(TestContext.Current.CancellationToken);

        interrupted.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => firstAttempt);
        Assert.Equal(
            ExecutionStatus.Running,
            Assert.IsType<ExecutionSnapshot>(await store.GetAsync(
                submitted.ExecutionId,
                TestContext.Current.CancellationToken)).Status);
        Assert.Equal(
            WorkflowStepStatus.Running,
            Assert.Single(Assert.IsType<WorkflowCheckpoint>(await checkpoints.GetAsync(
                submitted.ExecutionId,
                TestContext.Current.CancellationToken)).Steps).Status);

        Assert.True(await work.AbandonAsync(
            firstLease,
            DateTimeOffset.UtcNow,
            TestContext.Current.CancellationToken));
        var takeover = Assert.IsType<ExecutionLease>(await work.TryClaimAsync(
            "worker-b",
            DateTimeOffset.UtcNow.AddSeconds(1),
            TimeSpan.FromSeconds(30),
            TestContext.Current.CancellationToken));
        var completed = await orchestrator.ProcessQueuedAsync(
            submitted.ExecutionId,
            TestContext.Current.CancellationToken);
        Assert.True(await work.CompleteAsync(
            takeover,
            DateTimeOffset.UtcNow,
            TestContext.Current.CancellationToken));

        Assert.Equal(ExecutionStatus.Succeeded, completed.Status);
        Assert.Equal(2, gateway.InvocationCount);
        Assert.Contains(
            await ReadEventsAsync(store, submitted.ExecutionId),
            item => item.Type == ExecutionEventTypes.WorkflowResumed);
    }

    [Fact]
    public async Task PersistedCancellationInterruptsAQueuedExecutionWhileItIsRunning()
    {
        var store = new InMemoryExecutionStore();
        var checkpoints = new InMemoryWorkflowCheckpointStore();
        var gateway = new InterruptOnceModelGateway();
        var work = new InMemoryExecutionWorkStore(store);
        using var cancellations = new ExecutionCancellationRegistry();
        var orchestrator = CreateOrchestrator(
            store,
            new InMemoryArtifactStore(),
            cancellations,
            gateway,
            checkpoints: checkpoints,
            executionWork: work);
        var submitted = await orchestrator.SubmitAsync(
            CreateRequest("tenant-a", "running-cancellation"),
            TestContext.Current.CancellationToken);
        var lease = Assert.IsType<ExecutionLease>(await work.TryClaimAsync(
            "cancellation-worker",
            DateTimeOffset.UtcNow,
            TimeSpan.FromSeconds(30),
            TestContext.Current.CancellationToken));
        using var heartbeatCancellation = new CancellationTokenSource();
        var processing = orchestrator.ProcessQueuedAsync(
            submitted.ExecutionId,
            heartbeatCancellation.Token);
        await gateway.FirstAttemptStarted.Task.WaitAsync(TestContext.Current.CancellationToken);
        var running = Assert.IsType<ExecutionSnapshot>(await store.GetAsync(
            submitted.ExecutionId,
            TestContext.Current.CancellationToken));
        var cancellationAt = DateTimeOffset.UtcNow;
        Assert.True(await store.TryRequestCancellationAsync(
            running.ExecutionId,
            cancellationAt,
            TestContext.Current.CancellationToken));

        heartbeatCancellation.Cancel();
        var cancelled = await processing;
        Assert.True(await work.CompleteAsync(
            lease,
            DateTimeOffset.UtcNow,
            TestContext.Current.CancellationToken));

        Assert.Equal(ExecutionStatus.Cancelled, cancelled.Status);
        Assert.Equal(
            WorkflowStepStatus.Cancelled,
            Assert.Single(Assert.IsType<WorkflowCheckpoint>(await checkpoints.GetAsync(
                submitted.ExecutionId,
                TestContext.Current.CancellationToken)).Steps).Status);
        Assert.Equal(1, gateway.InvocationCount);
    }

    [Fact]
    public async Task PostgresWorkersProveClaimCrashTakeoverRecoveryAndCancellation()
    {
        var connectionString = Environment.GetEnvironmentVariable("IROUTE_TEST_POSTGRES");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            return;
        }

        var factory = new PostgresContextFactory(connectionString);
        await using (var reset = factory.CreateDbContext())
        {
            await reset.Database.EnsureDeletedAsync(TestContext.Current.CancellationToken);
        }

        try
        {
            await new SchemaMigrationManager(factory).UpgradeAsync(
                cancellationToken: TestContext.Current.CancellationToken);
            var store = new EfExecutionStore(factory, new NullExecutionFence());
            var checkpoints = new EfWorkflowCheckpointStore(factory);
            var gateway = new InterruptOnceModelGateway();
            var work = new EfExecutionWorkStore(factory);
            using var cancellations = new ExecutionCancellationRegistry();
            var orchestrator = CreateOrchestrator(
                store,
                new EfArtifactStore(factory),
                cancellations,
                gateway,
                checkpoints: checkpoints,
                memories: new EfMemoryStore(factory),
                executionWork: work);
            var submitted = await orchestrator.SubmitAsync(
                CreateRequest("postgres-tenant", "postgres-takeover"),
                TestContext.Current.CancellationToken);

            var claims = await Task.WhenAll(
                new EfExecutionWorkStore(factory).TryClaimAsync(
                    "postgres-worker-a",
                    DateTimeOffset.UtcNow,
                    TimeSpan.FromSeconds(30),
                    TestContext.Current.CancellationToken),
                new EfExecutionWorkStore(factory).TryClaimAsync(
                    "postgres-worker-b",
                    DateTimeOffset.UtcNow,
                    TimeSpan.FromSeconds(30),
                    TestContext.Current.CancellationToken));
            var firstLease = Assert.IsType<ExecutionLease>(Assert.Single(
                claims,
                claim => claim is not null));
            using var interrupted = new CancellationTokenSource();
            var firstAttempt = orchestrator.ProcessQueuedAsync(submitted.ExecutionId, interrupted.Token);
            await gateway.FirstAttemptStarted.Task.WaitAsync(TestContext.Current.CancellationToken);
            interrupted.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => firstAttempt);

            var takeover = Assert.IsType<ExecutionLease>(await work.TryClaimAsync(
                "postgres-worker-takeover",
                firstLease.ExpiresAt.AddMilliseconds(1),
                TimeSpan.FromSeconds(30),
                TestContext.Current.CancellationToken));
            Assert.Equal(2, takeover.DeliveryAttempt);
            Assert.False(await work.CompleteAsync(
                firstLease,
                DateTimeOffset.UtcNow,
                TestContext.Current.CancellationToken));
            var recovered = await orchestrator.ProcessQueuedAsync(
                submitted.ExecutionId,
                TestContext.Current.CancellationToken);
            Assert.True(await work.CompleteAsync(
                takeover,
                DateTimeOffset.UtcNow,
                TestContext.Current.CancellationToken));
            Assert.Equal(ExecutionStatus.Succeeded, recovered.Status);
            await Task.WhenAll(Enumerable.Range(0, 8).Select(index =>
                store.AppendEventAsync(
                    submitted.ExecutionId,
                    ExecutionEventTypes.LeaseRenewed,
                    DateTimeOffset.UtcNow,
                    JsonSerializer.SerializeToElement(new { workerId = $"concurrent-{index}" }),
                    TestContext.Current.CancellationToken)));
            var recoveredEvents = await ReadEventsAsync(store, submitted.ExecutionId);
            Assert.Contains(
                recoveredEvents,
                item => item.Type == ExecutionEventTypes.WorkflowResumed);
            Assert.Equal(
                Enumerable.Range(1, recoveredEvents.Count).Select(index => (long)index),
                recoveredEvents.Select(item => item.Sequence));

            var cancellationRequest = CreateRequest(
                "postgres-tenant",
                "postgres-cancellation") with
            {
                Input = JsonSerializer.SerializeToElement(new
                {
                    recipient = new { name = "Grace" },
                    projectName = "iRoute",
                    objective = "Prove distributed cancellation through a database heartbeat.",
                    tone = "concise"
                })
            };
            var cancellable = await orchestrator.SubmitAsync(
                cancellationRequest,
                TestContext.Current.CancellationToken);
            var cancellationLease = Assert.IsType<ExecutionLease>(await work.TryClaimAsync(
                "postgres-cancellation-worker",
                DateTimeOffset.UtcNow,
                TimeSpan.FromSeconds(30),
                TestContext.Current.CancellationToken));
            var cancellationAt = DateTimeOffset.UtcNow;
            Assert.True(await store.TryRequestCancellationAsync(
                cancellable.ExecutionId,
                cancellationAt,
                TestContext.Current.CancellationToken));
            var heartbeat = await work.RenewAsync(
                cancellationLease,
                cancellationAt,
                TimeSpan.FromSeconds(30),
                TestContext.Current.CancellationToken);
            Assert.True(heartbeat.Renewed);
            Assert.True(heartbeat.CancellationRequested);
            var cancelled = await orchestrator.ProcessQueuedAsync(
                cancellable.ExecutionId,
                TestContext.Current.CancellationToken);
            Assert.True(await work.CompleteAsync(
                cancellationLease,
                DateTimeOffset.UtcNow,
                TestContext.Current.CancellationToken));
            Assert.Equal(ExecutionStatus.Cancelled, cancelled.Status);
        }
        finally
        {
            await using var reset = factory.CreateDbContext();
            await reset.Database.EnsureDeletedAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task RawHistoryIsProjectedOutBeforeTheModelGateway()
    {
        var store = new InMemoryExecutionStore();
        var artifacts = new InMemoryArtifactStore();
        var gateway = new CapturingModelGateway();
        using var cancellations = new ExecutionCancellationRegistry();
        var orchestrator = CreateOrchestrator(store, artifacts, cancellations, gateway);
        var request = CreateRequest("tenant-a", "bounded-history") with
        {
            Input = JsonSerializer.SerializeToElement(new
            {
                recipient = new { name = "Ada" },
                projectName = "iRoute",
                objective = "Share a bounded project update.",
                tone = "professional",
                activeDecisions = ActiveDecisions,
                projectHistory = Enumerable.Range(0, 10)
                    .Select(index => $"Raw historical event {index}.")
                    .ToArray()
            })
        };

        var result = await orchestrator.ExecuteAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(ExecutionStatus.Succeeded, result.Status);
        var gatewayRequest = Assert.IsType<ModelGatewayRequest>(gateway.LastRequest);
        Assert.False(gatewayRequest.Input.TryGetProperty("activeDecisions", out _));
        Assert.False(gatewayRequest.Input.TryGetProperty("projectHistory", out _));
        Assert.Equal(3, gatewayRequest.Context.GetProperty("projectHistory").GetArrayLength());
        var manifest = Assert.IsType<ContextManifest>(Assert.IsType<TaskOutcome>(result.Outcome).Context);
        Assert.False(manifest.FullHistoryIncluded);
        Assert.True(manifest.Truncated);
    }

    [Fact]
    public async Task ReadOnlyConnectorsCompleteThroughTheTaskOrchestrator()
    {
        var cases = new[]
        {
            new
            {
                TaskType = "calendar.find_slots",
                Scope = "calendar:read",
                Input = JsonSerializer.SerializeToElement(new { timezone = "Europe/Tirane" }),
                ExpectedProperty = "slots"
            },
            new
            {
                TaskType = "database.answer",
                Scope = "database:read",
                Input = JsonSerializer.SerializeToElement(new { queryId = "project-status" }),
                ExpectedProperty = "answer"
            }
        };

        foreach (var item in cases)
        {
            var store = new InMemoryExecutionStore();
            using var cancellations = new ExecutionCancellationRegistry();
            var orchestrator = CreateOrchestrator(store, new InMemoryArtifactStore(), cancellations);
            var result = await orchestrator.ExecuteAsync(
                new TaskRequest(
                    item.TaskType,
                    item.Input,
                    ProjectId: "project-1",
                    IdempotencyKey: $"{item.TaskType}-connector",
                    Constraints: new TaskConstraints(MaxModelCalls: 0, MaxToolCalls: 1),
                    TenantId: "tenant-a",
                    ActorId: "test-runner",
                    PermissionScopes: [item.Scope]),
                TestContext.Current.CancellationToken);

            Assert.Equal(ExecutionStatus.Succeeded, result.Status);
            var outcome = Assert.IsType<TaskOutcome>(result.Outcome);
            Assert.Equal(ResolutionLevel.DeterministicCapability, outcome.ResolutionLevel);
            Assert.Equal(0, outcome.Usage.ModelCalls);
            Assert.Equal(1, outcome.Usage.ToolCalls);
            Assert.True(outcome.Output.TryGetProperty(item.ExpectedProperty, out _));
            Assert.NotEmpty(outcome.Evidence);
            var events = await ReadEventsAsync(store, result.ExecutionId);
            Assert.Contains(events, entry => entry.Type == ExecutionEventTypes.CapabilityStarted);
            var completed = Assert.Single(events, entry => entry.Type == ExecutionEventTypes.CapabilityCompleted);
            Assert.True(completed.Data.GetProperty("projected").GetBoolean());
            Assert.False(string.IsNullOrWhiteSpace(
                completed.Data.GetProperty("connectorId").GetString()));
        }
    }

    [Fact]
    public async Task OnlyProjectedConnectorOutputEntersDependentModelContext()
    {
        var definition = new TaskDefinition(
            "email.read_then_summarize",
            1,
            "text.generation",
            800,
            0.8m,
            true,
            SideEffectClass.ReadOnly,
            "email.summary",
            DefaultMaxInputTokens: 4000,
            DefaultDeadlineMilliseconds: 30000,
            DefaultMaxModelCalls: 1,
            AllowedCapabilities: ["email.read", "text.generation"],
            PermissionScopes: ["email:read"],
            RequiredCapabilities: ["email.read", "text.generation"],
            DefaultMaxToolCalls: 1,
            DefaultMaxTaskDepth: 2);
        var plan = new ExecutionPlan(
            "email.read_then_summarize@1:test",
            1,
            definition.TaskType,
            definition.Version,
            [
                new ExecutionPlanStep(
                    "read-email",
                    ExecutionStepKind.Tool,
                    "email.read",
                    [],
                    SideEffectClass.ReadOnly,
                    5000),
                new ExecutionPlanStep(
                    "execute",
                    ExecutionStepKind.Model,
                    "text.generation",
                    ["read-email"],
                    SideEffectClass.None,
                    5000,
                    ProfileId: "text.generation.small.eval-v1")
            ],
            new ExecutionPlanBudget(2, 1, 1, 1, 2, 30000, 4000, 800));
        var gateway = new CapturingModelGateway();
        var store = new InMemoryExecutionStore();
        using var cancellations = new ExecutionCancellationRegistry();
        var orchestrator = CreateOrchestrator(
            store,
            new InMemoryArtifactStore(),
            cancellations,
            gateway,
            new FixedTaskRouter(plan),
            taskDefinitions: new SingleTaskDefinitionRegistry(definition));

        var result = await orchestrator.ExecuteAsync(
            new TaskRequest(
                definition.TaskType,
                JsonSerializer.SerializeToElement(new { query = "project status" }),
                ProjectId: "project-1",
                IdempotencyKey: "read-and-summarize",
                Constraints: new TaskConstraints(MaxModelCalls: 1, MaxToolCalls: 1, MaxTaskDepth: 2),
                TenantId: "tenant-a",
                ActorId: "test-runner",
                PermissionScopes: ["email:read"]),
            TestContext.Current.CancellationToken);

        Assert.Equal(ExecutionStatus.Succeeded, result.Status);
        var gatewayRequest = Assert.IsType<ModelGatewayRequest>(gateway.LastRequest);
        var capabilityOutput = gatewayRequest.Context
            .GetProperty("capabilityOutputs")
            .GetProperty("read-email");
        var message = capabilityOutput.GetProperty("messages")[0];
        Assert.True(message.TryGetProperty("snippet", out _));
        Assert.False(message.TryGetProperty("body", out _));
        Assert.False(message.TryGetProperty("headers", out _));
        Assert.False(capabilityOutput.TryGetProperty("metadata", out _));
        Assert.DoesNotContain("IGNORE", gatewayRequest.Context.GetRawText(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ModelFirstReadOnlyWorkflowAggregatesUsageAcrossFinalToolStep()
    {
        var definition = ModelThenCalendarDefinition();
        var plan = ModelThenCalendarPlan(definition);
        var store = new InMemoryExecutionStore();
        using var cancellations = new ExecutionCancellationRegistry();
        var orchestrator = CreateOrchestrator(
            store,
            new InMemoryArtifactStore(),
            cancellations,
            taskRouter: new FixedTaskRouter(plan),
            taskDefinitions: new SingleTaskDefinitionRegistry(definition));

        var result = await orchestrator.ExecuteAsync(
            ModelThenCalendarRequest(definition.TaskType, "model-tool-usage"),
            TestContext.Current.CancellationToken);

        Assert.Equal(ExecutionStatus.Succeeded, result.Status);
        var outcome = Assert.IsType<TaskOutcome>(result.Outcome);
        Assert.Equal(1, outcome.Usage.ModelCalls);
        Assert.Equal(1, outcome.Usage.ToolCalls);
        Assert.True(outcome.Usage.InputTokens > 0);
        var events = await ReadEventsAsync(store, result.ExecutionId);
        Assert.Single(events, item => item.Type == ExecutionEventTypes.GatewayCompleted);
    }

    [Fact]
    public async Task FinalToolCannotHideModelCostFromWorkflowBudget()
    {
        var definition = ModelThenCalendarDefinition();
        var plan = ModelThenCalendarPlan(definition) with
        {
            Budget = ModelThenCalendarPlan(definition).Budget with { MaxCost = 1m }
        };
        var store = new InMemoryExecutionStore();
        using var cancellations = new ExecutionCancellationRegistry();
        var orchestrator = CreateOrchestrator(
            store,
            new InMemoryArtifactStore(),
            cancellations,
            new FixedCostModelGateway(0.5m),
            new FixedTaskRouter(plan),
            taskDefinitions: new SingleTaskDefinitionRegistry(definition));
        var request = ModelThenCalendarRequest(definition.TaskType, "model-tool-cost") with
        {
            Constraints = new TaskConstraints(
                MaxCost: 0.25m,
                MaxModelCalls: 1,
                MaxToolCalls: 1,
                MaxTaskDepth: 2)
        };

        var result = await orchestrator.ExecuteAsync(
            request,
            TestContext.Current.CancellationToken);

        Assert.Equal(ExecutionStatus.Failed, result.Status);
        Assert.Equal(ErrorCodes.CostBudgetExceeded, Assert.IsType<Problem>(result.Error).Code);
    }

    [Fact]
    public async Task OversizedEssentialInputFailsBeforeTheModelGateway()
    {
        var store = new InMemoryExecutionStore();
        var artifacts = new InMemoryArtifactStore();
        var gateway = new CountingModelGateway();
        using var cancellations = new ExecutionCancellationRegistry();
        var orchestrator = CreateOrchestrator(store, artifacts, cancellations, gateway);
        var request = CreateRequest("tenant-a", "oversized-context-input") with
        {
            Input = JsonSerializer.SerializeToElement(new
            {
                recipient = new { name = "Ada" },
                objective = new string('x', 1000),
                activeDecisions = ActiveDecisions
            }),
            Constraints = new TaskConstraints(MaxInputTokens: 20, MaxModelCalls: 1)
        };

        var result = await orchestrator.ExecuteAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(ExecutionStatus.Failed, result.Status);
        Assert.Equal(ErrorCodes.ContextBudgetExceeded, Assert.IsType<Problem>(result.Error).Code);
        Assert.Equal(0, gateway.InvocationCount);
    }

    [Fact]
    public async Task ChangedDecisionInvalidatesDependentArtifactAndNewVersionIsReusableWithoutGeneration()
    {
        var store = new InMemoryExecutionStore();
        var artifacts = new InMemoryArtifactStore();
        var memories = new InMemoryMemoryStore();
        var gateway = new CountingModelGateway();
        using var cancellations = new ExecutionCancellationRegistry();
        var orchestrator = CreateOrchestrator(
            store,
            artifacts,
            cancellations,
            gateway,
            memories: memories);

        var first = await orchestrator.ExecuteAsync(
            CreateRequest("tenant-a", "decision-v1"),
            TestContext.Current.CancellationToken);
        var firstArtifactId = Assert.Single(Assert.IsType<TaskOutcome>(first.Outcome).Artifacts).ArtifactId;
        var firstDecision = await memories.GetActiveAsync(
            new MemoryLookup(
                "tenant-a",
                "project-1",
                MemoryKind.Decision,
                "activeDecisions[0]",
                DateTimeOffset.UtcNow),
            TestContext.Current.CancellationToken);
        Assert.NotNull(firstDecision);

        var changedRequest = CreateRequest("tenant-a", "decision-v2") with
        {
            Input = JsonSerializer.SerializeToElement(new
            {
                recipient = new { name = "Ada" },
                projectName = "iRoute",
                objective = "Share the first working runtime milestone.",
                tone = "professional",
                activeDecisions = ChangedDecisions
            })
        };
        var second = await orchestrator.ExecuteAsync(
            changedRequest,
            TestContext.Current.CancellationToken);
        var secondOutcome = Assert.IsType<TaskOutcome>(second.Outcome);
        var secondArtifactReference = Assert.Single(secondOutcome.Artifacts);

        var invalidated = await artifacts.GetAsync(
            "tenant-a",
            firstArtifactId,
            TestContext.Current.CancellationToken);
        Assert.NotNull(invalidated);
        Assert.Equal(ArtifactLifecycleStatus.Invalidated, invalidated.LifecycleStatus);
        Assert.False(invalidated.IsActive);
        Assert.Equal(2, secondArtifactReference.Version);
        Assert.NotEqual(firstArtifactId, secondArtifactReference.ArtifactId);
        Assert.Contains(
            (await artifacts.GetAsync(
                "tenant-a",
                secondArtifactReference.ArtifactId,
                TestContext.Current.CancellationToken))!.EffectiveDependencies,
            dependency =>
                dependency.Kind == "memory" &&
                dependency.Reference != firstDecision.MemoryId.ToString());

        var events = await ReadEventsAsync(store, second.ExecutionId);
        Assert.Contains(events, item => item.Type == ExecutionEventTypes.MemorySuperseded);
        Assert.Contains(events, item => item.Type == ExecutionEventTypes.ArtifactInvalidated);
        Assert.Contains(events, item => item.Type == ExecutionEventTypes.ArtifactMaterialized);

        var third = await orchestrator.ExecuteAsync(
            changedRequest with { IdempotencyKey = "decision-v2-reuse" },
            TestContext.Current.CancellationToken);
        var thirdOutcome = Assert.IsType<TaskOutcome>(third.Outcome);
        Assert.Equal(ResolutionLevel.ExactArtifact, thirdOutcome.ResolutionLevel);
        Assert.Equal(0, thirdOutcome.Usage.ModelCalls);
        Assert.Equal(secondArtifactReference.ArtifactId, Assert.Single(thirdOutcome.Artifacts).ArtifactId);
        Assert.Equal(2, gateway.InvocationCount);
    }

    [Fact]
    public async Task KnownProjectDecisionResolvesFromStructuredStateWithoutGeneration()
    {
        var store = new InMemoryExecutionStore();
        var artifacts = new InMemoryArtifactStore();
        var memories = new InMemoryMemoryStore();
        var gateway = new CountingModelGateway();
        using var cancellations = new ExecutionCancellationRegistry();
        var orchestrator = CreateOrchestrator(
            store,
            artifacts,
            cancellations,
            gateway,
            memories: memories);

        var seed = await orchestrator.ExecuteAsync(
            CreateRequest("tenant-a", "seed-decision"),
            TestContext.Current.CancellationToken);
        Assert.Equal(ExecutionStatus.Succeeded, seed.Status);

        var result = await orchestrator.ExecuteAsync(
            CreateDecisionRequest("tenant-a", "read-decision", ["project:read"]),
            TestContext.Current.CancellationToken);

        Assert.Equal(ExecutionStatus.Succeeded, result.Status);
        var outcome = Assert.IsType<TaskOutcome>(result.Outcome);
        Assert.Equal(ResolutionLevel.StructuredState, outcome.ResolutionLevel);
        Assert.Equal(0, outcome.Usage.ModelCalls);
        Assert.Equal(1, gateway.InvocationCount);
        Assert.Equal(
            ActiveDecisions[0],
            outcome.Output.GetProperty("value").GetString());
        Assert.Equal("activeDecisions[0]", outcome.Output.GetProperty("key").GetString());
        Assert.Equal(1, outcome.Output.GetProperty("version").GetInt32());
        Assert.True(outcome.Output.TryGetProperty("contentHash", out _));
        Assert.True(outcome.Output.TryGetProperty("createdAt", out _));
        Assert.False(outcome.Output.TryGetProperty("Key", out _));
        var events = await ReadEventsAsync(store, result.ExecutionId);
        var exactMiss = Assert.Single(events, item =>
            item.Type == ExecutionEventTypes.ResolutionConsidered &&
            item.Data.GetProperty("resolver").GetString() == "exact-cache");
        Assert.False(exactMiss.Data.GetProperty("accepted").GetBoolean());
        Assert.Equal(
            ResolutionDecisionCodes.ExactCacheMiss,
            exactMiss.Data.GetProperty("code").GetString());
        Assert.False(string.IsNullOrWhiteSpace(exactMiss.Data.GetProperty("reason").GetString()));
        var stateHit = Assert.Single(events, item =>
            item.Type == ExecutionEventTypes.ResolutionConsidered &&
            item.Data.GetProperty("resolver").GetString() == "fact-decision");
        Assert.True(stateHit.Data.GetProperty("accepted").GetBoolean());
        Assert.True(stateHit.Data.GetProperty("permissionChecked").GetBoolean());
        Assert.True(stateHit.Data.GetProperty("freshnessChecked").GetBoolean());
        Assert.Equal(
            ResolutionDecisionCodes.StateHit,
            stateHit.Data.GetProperty("code").GetString());
        Assert.DoesNotContain(events, item => item.Type == ExecutionEventTypes.PlanValidated);
    }

    [Fact]
    public async Task MissingPermissionRejectsProjectDecisionBeforeStateLookupOrGeneration()
    {
        var store = new InMemoryExecutionStore();
        var artifacts = new InMemoryArtifactStore();
        var memories = new InMemoryMemoryStore();
        var gateway = new CountingModelGateway();
        using var cancellations = new ExecutionCancellationRegistry();
        var orchestrator = CreateOrchestrator(
            store,
            artifacts,
            cancellations,
            gateway,
            memories: memories);
        await orchestrator.ExecuteAsync(
            CreateRequest("tenant-a", "seed-protected-decision"),
            TestContext.Current.CancellationToken);

        var result = await orchestrator.ExecuteAsync(
            CreateDecisionRequest("tenant-a", "read-without-scope", []),
            TestContext.Current.CancellationToken);

        Assert.Equal(ExecutionStatus.Failed, result.Status);
        Assert.Equal(ErrorCodes.PermissionScopeDenied, Assert.IsType<Problem>(result.Error).Code);
        Assert.Equal(1, gateway.InvocationCount);
        var events = await ReadEventsAsync(store, result.ExecutionId);
        var decisions = events
            .Where(item => item.Type == ExecutionEventTypes.ResolutionConsidered)
            .ToArray();
        Assert.Equal(4, decisions.Length);
        Assert.All(decisions, item =>
        {
            Assert.False(item.Data.GetProperty("accepted").GetBoolean());
            Assert.True(item.Data.GetProperty("permissionChecked").GetBoolean());
            Assert.Equal(
                ResolutionDecisionCodes.PermissionDenied,
                item.Data.GetProperty("code").GetString());
            Assert.False(string.IsNullOrWhiteSpace(item.Data.GetProperty("reason").GetString()));
        });
    }

    [Fact]
    public async Task ExplicitArtifactLookupReturnsValidatedContentWithoutGeneration()
    {
        var store = new InMemoryExecutionStore();
        var artifacts = new InMemoryArtifactStore();
        var memories = new InMemoryMemoryStore();
        var gateway = new CountingModelGateway();
        using var cancellations = new ExecutionCancellationRegistry();
        var orchestrator = CreateOrchestrator(
            store,
            artifacts,
            cancellations,
            gateway,
            memories: memories);
        var seed = await orchestrator.ExecuteAsync(
            CreateRequest("tenant-a", "seed-artifact"),
            TestContext.Current.CancellationToken);
        var artifact = Assert.Single(Assert.IsType<TaskOutcome>(seed.Outcome).Artifacts);
        var request = new TaskRequest(
            "email.draft",
            JsonSerializer.SerializeToElement(new { artifactId = artifact.ArtifactId }),
            ProjectId: "project-1",
            IdempotencyKey: "explicit-artifact",
            TenantId: "tenant-a",
            ActorId: "test-runner");

        var result = await orchestrator.ExecuteAsync(request, TestContext.Current.CancellationToken);

        var outcome = Assert.IsType<TaskOutcome>(result.Outcome);
        Assert.Equal(ExecutionStatus.Succeeded, result.Status);
        Assert.Equal(ResolutionLevel.ExactArtifact, outcome.ResolutionLevel);
        Assert.Equal(artifact.ArtifactId, Assert.Single(outcome.Artifacts).ArtifactId);
        Assert.Equal(0, outcome.Usage.ModelCalls);
        Assert.Equal(1, gateway.InvocationCount);
        var events = await ReadEventsAsync(store, result.ExecutionId);
        var lookup = Assert.Single(events, item =>
            item.Type == ExecutionEventTypes.ResolutionConsidered &&
            item.Data.GetProperty("resolver").GetString() == "artifact-lookup");
        Assert.True(lookup.Data.GetProperty("accepted").GetBoolean());
        Assert.Equal(
            ResolutionDecisionCodes.ArtifactHit,
            lookup.Data.GetProperty("code").GetString());
    }

    [Fact]
    public async Task ArtifactReuseIsIsolatedByTenant()
    {
        var store = new InMemoryExecutionStore();
        var artifacts = new InMemoryArtifactStore();
        using var cancellations = new ExecutionCancellationRegistry();
        var orchestrator = CreateOrchestrator(store, artifacts, cancellations);

        var first = await orchestrator.ExecuteAsync(CreateRequest("tenant-a", "first"), TestContext.Current.CancellationToken);
        var second = await orchestrator.ExecuteAsync(CreateRequest("tenant-b", "second"), TestContext.Current.CancellationToken);

        var firstOutcome = Assert.IsType<TaskOutcome>(first.Outcome);
        var secondOutcome = Assert.IsType<TaskOutcome>(second.Outcome);
        Assert.Equal(ResolutionLevel.SmallModel, secondOutcome.ResolutionLevel);
        Assert.NotEqual(
            Assert.Single(firstOutcome.Artifacts).ArtifactId,
            Assert.Single(secondOutcome.Artifacts).ArtifactId);
    }

    [Fact]
    public async Task IdempotencyKeyReturnsTheOriginalExecutionWithinTenant()
    {
        var store = new InMemoryExecutionStore();
        var artifacts = new InMemoryArtifactStore();
        using var cancellations = new ExecutionCancellationRegistry();
        var orchestrator = CreateOrchestrator(store, artifacts, cancellations);
        var request = CreateRequest("tenant-a", "same-key");

        var first = await orchestrator.ExecuteAsync(request, TestContext.Current.CancellationToken);
        var second = await orchestrator.ExecuteAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(first.ExecutionId, second.ExecutionId);
    }

    [Fact]
    public async Task UnsupportedQualityFloorFailsBeforeGeneration()
    {
        var store = new InMemoryExecutionStore();
        var artifacts = new InMemoryArtifactStore();
        var memories = new InMemoryMemoryStore();
        using var cancellations = new ExecutionCancellationRegistry();
        var orchestrator = CreateOrchestrator(
            store,
            artifacts,
            cancellations,
            memories: memories);
        var request = CreateRequest("tenant-a", "quality") with
        {
            Constraints = new TaskConstraints(MinimumQuality: 0.99m)
        };

        var result = await orchestrator.ExecuteAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(ExecutionStatus.Failed, result.Status);
        Assert.Equal(ErrorCodes.RoutingNoEligibleCapability, Assert.IsType<Problem>(result.Error).Code);
        Assert.Null(await memories.GetActiveAsync(
            new MemoryLookup(
                "tenant-a",
                "project-1",
                MemoryKind.Decision,
                "activeDecisions[0]",
                DateTimeOffset.UtcNow),
            TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task HigherQualityFloorEscalatesToStrongProfileAndExplainsDecision()
    {
        var store = new InMemoryExecutionStore();
        var artifacts = new InMemoryArtifactStore();
        var gateway = new CapturingModelGateway();
        using var cancellations = new ExecutionCancellationRegistry();
        var orchestrator = CreateOrchestrator(store, artifacts, cancellations, gateway);
        var request = CreateRequest("tenant-a", "strong-route") with
        {
            Constraints = new TaskConstraints(MinimumQuality: 0.9m, MaxModelCalls: 1)
        };

        var result = await orchestrator.ExecuteAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(ExecutionStatus.Succeeded, result.Status);
        var outcome = Assert.IsType<TaskOutcome>(result.Outcome);
        Assert.Equal(ResolutionLevel.StrongModel, outcome.ResolutionLevel);
        var routing = Assert.IsType<RoutingDecision>(outcome.Routing);
        Assert.Equal("text.generation.strong.eval-v1", routing.SelectedProfileId);
        Assert.True(routing.Escalated);
        Assert.Contains("lower-cost route", routing.EscalationReason);
        Assert.Equal(routing.SelectedProfileId, Assert.IsType<ModelGatewayRequest>(gateway.LastRequest).ProfileId);
        var events = await ReadEventsAsync(store, result.ExecutionId);
        Assert.Single(events, item => item.Type == ExecutionEventTypes.RoutingDecided);
        var escalation = Assert.Single(events, item => item.Type == ExecutionEventTypes.RoutingEscalated);
        Assert.True(escalation.Data.GetProperty("escalated").GetBoolean());
        Assert.Equal(
            "text.generation.strong.eval-v1",
            escalation.Data.GetProperty("selectedProfileId").GetString());
    }

    [Fact]
    public async Task ZeroModelCallBudgetFailsBeforeGeneration()
    {
        var store = new InMemoryExecutionStore();
        var artifacts = new InMemoryArtifactStore();
        using var cancellations = new ExecutionCancellationRegistry();
        var orchestrator = CreateOrchestrator(store, artifacts, cancellations);
        var request = CreateRequest("tenant-a", "budget") with
        {
            Constraints = new TaskConstraints(MaxModelCalls: 0)
        };

        var result = await orchestrator.ExecuteAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(ExecutionStatus.Failed, result.Status);
        Assert.Equal("model_budget_exhausted", Assert.IsType<Problem>(result.Error).Code);
    }

    [Fact]
    public async Task GatewayFailureProducesStableRetryableProblem()
    {
        var store = new InMemoryExecutionStore();
        var artifacts = new InMemoryArtifactStore();
        using var cancellations = new ExecutionCancellationRegistry();
        var orchestrator = CreateOrchestrator(
            store,
            artifacts,
            cancellations,
            new ThrowingModelGateway());

        var result = await orchestrator.ExecuteAsync(
            CreateRequest("tenant-a", "gateway-failure"),
            TestContext.Current.CancellationToken);

        Assert.Equal(ExecutionStatus.Failed, result.Status);
        var problem = Assert.IsType<Problem>(result.Error);
        Assert.Equal("model_gateway_http_error", problem.Code);
        Assert.True(problem.Retryable);
        var metadata = Assert.IsType<Dictionary<string, string>>(problem.Metadata);
        Assert.Equal("503", metadata["gatewayStatusCode"]);
        Assert.Equal(ModelGatewayFailureKind.Unavailable.ToString(), metadata["gatewayFailureKind"]);
        Assert.Equal("external-failure", metadata["gatewayId"]);
        var events = await ReadEventsAsync(store, result.ExecutionId);
        Assert.Equal(1, events.Count(item => item.Type == ExecutionEventTypes.GatewayStarted));
        Assert.DoesNotContain(events, item => item.Type == ExecutionEventTypes.StepRetryScheduled);
        var failed = events.Last(item => item.Type == ExecutionEventTypes.GatewayFailed);
        Assert.Equal(1, events.Count(item => item.Type == ExecutionEventTypes.GatewayFailed));
        Assert.Equal("Unavailable", failed.Data.GetProperty("kind").GetString());
        Assert.True(failed.Data.GetProperty("retryable").GetBoolean());
        Assert.Equal(503, failed.Data.GetProperty("statusCode").GetInt32());
    }

    [Fact]
    public async Task GatewayFallbackIsTracedWithoutWorkflowLayerRetries()
    {
        var store = new InMemoryExecutionStore();
        var artifacts = new InMemoryArtifactStore();
        using var cancellations = new ExecutionCancellationRegistry();
        var deployments = new[]
        {
            TestGatewayDeployment("primary", "gateway-a", 0),
            TestGatewayDeployment("secondary", "gateway-b", 1)
        };
        var gateway = new ResilientModelGateway(
            new TestGatewayRegistry(deployments),
            new TestGatewayClientFactory(new Dictionary<string, IModelGateway>(StringComparer.Ordinal)
            {
                ["primary"] = new ThrowingModelGateway(),
                ["secondary"] = new DeterministicModelGateway()
            }),
            new InMemoryGatewayCircuitStore(),
            new SystemClock(),
            new GatewayResilienceOptions
            {
                MaximumAttempts = 2,
                Circuit = new GatewayCircuitPolicy(1, TimeSpan.FromSeconds(30))
            });
        var orchestrator = CreateOrchestrator(store, artifacts, cancellations, gateway);

        var result = await orchestrator.ExecuteAsync(
            CreateRequest("tenant-a", "gateway-fallback"),
            TestContext.Current.CancellationToken);

        Assert.Equal(ExecutionStatus.Succeeded, result.Status);
        var routing = Assert.IsType<RoutingDecision>(Assert.IsType<TaskOutcome>(result.Outcome).Routing);
        Assert.Equal("secondary", routing.SelectedDeployment?.DeploymentId);
        Assert.Equal(2, routing.Resilience?.Attempts.Count);
        var events = await ReadEventsAsync(store, result.ExecutionId);
        Assert.Equal(1, events.Count(item => item.Type == ExecutionEventTypes.GatewayStarted));
        Assert.DoesNotContain(events, item => item.Type == ExecutionEventTypes.StepRetryScheduled);
        Assert.Equal(2, events.Count(item => item.Type == ExecutionEventTypes.GatewayAttempted));
        Assert.Equal(1, events.Count(item => item.Type == ExecutionEventTypes.GatewayFallbackSelected));
        Assert.Equal(1, events.Count(item => item.Type == ExecutionEventTypes.GatewayCircuitChanged));
        Assert.Equal(1, events.Count(item => item.Type == ExecutionEventTypes.GatewayResilienceDecided));
        var completed = Assert.Single(events, item => item.Type == ExecutionEventTypes.GatewayCompleted);
        Assert.Equal("secondary", completed.Data.GetProperty("deploymentId").GetString());
        Assert.Equal(2, completed.Data.GetProperty("fallbackAttempts").GetInt32());
    }

    [Fact]
    public async Task StreamingGatewayReportsProgressAndObservedLatencyWithoutBufferingProviderDetails()
    {
        var store = new InMemoryExecutionStore();
        var artifacts = new InMemoryArtifactStore();
        using var cancellations = new ExecutionCancellationRegistry();
        var orchestrator = CreateOrchestrator(
            store,
            artifacts,
            cancellations,
            new StreamingModelGateway());

        var result = await orchestrator.ExecuteAsync(
            CreateRequest("tenant-a", "streaming-gateway"),
            TestContext.Current.CancellationToken);

        Assert.Equal(ExecutionStatus.Succeeded, result.Status);
        var outcome = Assert.IsType<TaskOutcome>(result.Outcome);
        Assert.True(outcome.Usage.DurationMilliseconds >= 10);
        Assert.Equal(1, outcome.Usage.ModelCalls);
        var events = await ReadEventsAsync(store, result.ExecutionId);
        var started = Assert.Single(events, item => item.Type == ExecutionEventTypes.GatewayStarted);
        Assert.Equal("external-stream", started.Data.GetProperty("gatewayId").GetString());
        Assert.Equal(30_000, started.Data.GetProperty("deadlineMilliseconds").GetInt32());
        var streamed = Assert.Single(events, item => item.Type == ExecutionEventTypes.GatewayStreamed);
        Assert.Equal("external-stream", streamed.Data.GetProperty("gatewayId").GetString());
        Assert.Equal(3, streamed.Data.GetProperty("streamEvents").GetInt32());
        Assert.Equal(1, streamed.Data.GetProperty("deltaEvents").GetInt32());
        Assert.Equal(1, streamed.Data.GetProperty("usageUpdates").GetInt32());
        var completed = Assert.Single(events, item => item.Type == ExecutionEventTypes.GatewayCompleted);
        Assert.Equal("external-stream", completed.Data.GetProperty("gatewayId").GetString());
        Assert.Equal("Streaming", completed.Data.GetProperty("transport").GetString());
        Assert.Equal(
            outcome.Usage.DurationMilliseconds,
            completed.Data.GetProperty("durationMilliseconds").GetInt64());
    }

    [Fact]
    public async Task InvalidPlanFailsBeforeCapabilityExecution()
    {
        var store = new InMemoryExecutionStore();
        var artifacts = new InMemoryArtifactStore();
        using var cancellations = new ExecutionCancellationRegistry();
        var gateway = new ThrowIfInvokedModelGateway();
        var invalidPlan = new ExecutionPlan(
            "email.draft@1:invalid",
            1,
            "email.draft",
            1,
            [
                new ExecutionPlanStep(
                    "execute",
                    ExecutionStepKind.Model,
                    "text.generation",
                    ["missing"],
                    SideEffectClass.None,
                    30_000)
            ],
            new ExecutionPlanBudget(1, 1, 0, 1, 1, 30_000));
        var orchestrator = CreateOrchestrator(
            store,
            artifacts,
            cancellations,
            gateway,
            new FixedTaskRouter(invalidPlan));

        var result = await orchestrator.ExecuteAsync(
            CreateRequest("tenant-a", "invalid-plan"),
            TestContext.Current.CancellationToken);

        Assert.Equal(ExecutionStatus.Failed, result.Status);
        Assert.Equal(ErrorCodes.InvalidExecutionPlan, Assert.IsType<Problem>(result.Error).Code);
        Assert.Equal(0, gateway.InvocationCount);
        var events = await ReadEventsAsync(store, result.ExecutionId);
        Assert.DoesNotContain(events, executionEvent => executionEvent.Type == ExecutionEventTypes.PlanValidated);
    }

    [Fact]
    public async Task SqlitePersistsExecutionsEventsAndReusableArtifactsAcrossRuntimeInstances()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"iroute-{Guid.NewGuid():N}.db");
        try
        {
            var factory = new SqliteContextFactory(databasePath);
            await using (var context = factory.CreateDbContext())
            {
                await context.Database.MigrateAsync(TestContext.Current.CancellationToken);
                var appliedMigrations = await context.Database.GetAppliedMigrationsAsync(
                    TestContext.Current.CancellationToken);
                Assert.Contains(
                    iRoute.Infrastructure.Migrations.InitialRuntimeStorage.MigrationId,
                    appliedMigrations);
                Assert.Contains(
                    iRoute.Infrastructure.Migrations.WorkflowCheckpoints.MigrationId,
                    appliedMigrations);
                Assert.Contains(
                    iRoute.Infrastructure.Migrations.PolicyApprovals.MigrationId,
                    appliedMigrations);
                Assert.Contains(
                    iRoute.Infrastructure.Migrations.ArtifactMemoryStore.MigrationId,
                    appliedMigrations);
            }

            ExecutionSnapshot first;
            using (var cancellations = new ExecutionCancellationRegistry())
            {
                var orchestrator = CreateOrchestrator(
                    new EfExecutionStore(factory, new NullExecutionFence()),
                    new EfArtifactStore(factory),
                    cancellations,
                    checkpoints: new EfWorkflowCheckpointStore(factory),
                    memories: new EfMemoryStore(factory));
                first = await orchestrator.ExecuteAsync(
                    CreateRequest("tenant-a", "persistent-first"),
                    TestContext.Current.CancellationToken);
            }

            var restartedStore = new EfExecutionStore(factory, new NullExecutionFence());
            using var restartedCancellations = new ExecutionCancellationRegistry();
            var restarted = CreateOrchestrator(
                restartedStore,
                new EfArtifactStore(factory),
                restartedCancellations,
                checkpoints: new EfWorkflowCheckpointStore(factory),
                memories: new EfMemoryStore(factory));
            var second = await restarted.ExecuteAsync(
                CreateRequest("tenant-a", "persistent-second"),
                TestContext.Current.CancellationToken);
            var decision = await restarted.ExecuteAsync(
                CreateDecisionRequest("tenant-a", "persistent-decision", ["project:read"]),
                TestContext.Current.CancellationToken);
            var persistedContext = await new BoundedContextCompiler(
                    new EfMemoryStore(factory),
                    new EfArtifactStore(factory),
                    new SystemClock())
                .CompileAsync(
                    new TaskRequest(
                        "email.draft",
                        JsonSerializer.SerializeToElement(new
                        {
                            objective = "Compile persisted project state after restart."
                        }),
                        ProjectId: "project-1",
                        TenantId: "tenant-a"),
                    await new BuiltInTaskDefinitionRegistry().FindAsync(
                        "email.draft",
                        TestContext.Current.CancellationToken)
                    ?? throw new InvalidOperationException("The email draft definition is missing."),
                    TestContext.Current.CancellationToken);

            Assert.Equal(ExecutionStatus.Succeeded, first.Status);
            Assert.Equal(ResolutionLevel.ExactArtifact, Assert.IsType<TaskOutcome>(second.Outcome).ResolutionLevel);
            Assert.Equal(
                ResolutionLevel.StructuredState,
                Assert.IsType<TaskOutcome>(decision.Outcome).ResolutionLevel);
            Assert.Equal(0, Assert.IsType<TaskOutcome>(decision.Outcome).Usage.ModelCalls);
            Assert.Contains(
                persistedContext.Content.GetProperty("activeDecisions").EnumerateArray(),
                item => item.GetString() == ActiveDecisions[0]);
            Assert.NotEmpty(persistedContext.Manifest.Provenance);
            Assert.NotEmpty(await ReadEventsAsync(restartedStore, first.ExecutionId));
        }
        finally
        {
            File.Delete(databasePath);
        }
    }

    internal static ExecutionOrchestrator CreateTestOrchestrator(
        IExecutionStore store,
        ExecutionCancellationRegistry cancellations) =>
        CreateOrchestrator(store, new InMemoryArtifactStore(), cancellations);

    private static ExecutionOrchestrator CreateOrchestrator(
        IExecutionStore store,
        IArtifactStore artifacts,
        ExecutionCancellationRegistry cancellations,
        IModelGateway? modelGateway = null,
        ITaskRouter? taskRouter = null,
        IWorkflowCheckpointStore? checkpoints = null,
        IApprovalStore? approvals = null,
        IExternalActionStore? externalActions = null,
        IExternalActionExecutor? externalActionExecutor = null,
        IMemoryStore? memories = null,
        IEnumerable<IDeterministicTaskHandler>? deterministicHandlers = null,
        ITaskDefinitionRegistry? taskDefinitions = null,
        IExecutionWorkStore? executionWork = null)
    {
        ITaskDefinitionRegistry definitions = taskDefinitions ?? new BuiltInTaskDefinitionRegistry();
        var fingerprint = new Sha256InputFingerprint();
        var clock = new SystemClock();
        checkpoints ??= new InMemoryWorkflowCheckpointStore();
        approvals ??= new InMemoryApprovalStore();
        externalActions ??= new InMemoryExternalActionStore();
        memories ??= new InMemoryMemoryStore();
        var scheduler = new BoundedDependencyScheduler(
            checkpoints,
            store,
            clock,
            new WorkflowSchedulerOptions());
        return new ExecutionOrchestrator(
            store,
            artifacts,
            new ProjectMemoryMaterializer(memories, artifacts, clock),
            [
                new ExactResultResolver(artifacts, fingerprint, clock),
                new FactDecisionResolver(memories, clock),
                new ArtifactLookupResolver(artifacts, clock),
                new DeterministicHandlerResolver(deterministicHandlers ?? [], clock)
            ],
            definitions,
            taskRouter ?? CreateTaskRouter(),
            new ExecutionPlanValidator(),
            new TaskPolicyEngine(),
            checkpoints,
            approvals,
            externalActions,
            scheduler,
            CreateCapabilityExecutor(clock),
            modelGateway ?? new DeterministicModelGateway(),
            externalActionExecutor ?? new DevelopmentExternalActionExecutor(),
            new BoundedContextCompiler(memories, artifacts, clock),
            [new EmailDraftOutcomeValidator(), new DefaultTaskOutcomeValidator()],
            fingerprint,
            cancellations,
            clock,
            executionWork: executionWork);
    }

    private static NormalizedCapabilityExecutor CreateCapabilityExecutor(IClock clock) =>
        new NormalizedCapabilityExecutor(
            new BuiltInCapabilityDefinitionRegistry(),
            [
                new ReferenceEmailConnector(),
                new ReferenceCalendarConnector(),
                new ReferenceDatabaseConnector(),
                new ReferenceOpenApiConnector(),
                new ReferenceMcpConnector(),
                new ReferenceAgentResultConnector(clock)
            ]);

    private static TaskRequest CreateRequest(string tenantId, string idempotencyKey) => new(
        "email.draft",
        JsonSerializer.SerializeToElement(new
        {
            recipient = new { name = "Ada" },
            projectName = "iRoute",
            objective = "Share the first working runtime milestone.",
            tone = "professional",
            activeDecisions = ActiveDecisions
        }),
        ProjectId: "project-1",
        IdempotencyKey: idempotencyKey,
        TenantId: tenantId,
        ActorId: "test-runner");

    private static TaskRequest CreateDecisionRequest(
        string tenantId,
        string idempotencyKey,
        IReadOnlyList<string> permissionScopes) => new(
        "project.decision.get",
        JsonSerializer.SerializeToElement(new { key = "activeDecisions[0]" }),
        ProjectId: "project-1",
        IdempotencyKey: idempotencyKey,
        Constraints: new TaskConstraints(MaxModelCalls: 0),
        TenantId: tenantId,
        ActorId: "test-runner",
        PermissionScopes: permissionScopes);

    private static TaskDefinition ModelThenCalendarDefinition() => new(
        "calendar.generate_then_read",
        1,
        "calendar.read",
        800,
        0.8m,
        true,
        SideEffectClass.ReadOnly,
        "calendar.generated-slots",
        DefaultMaxInputTokens: 4000,
        DefaultDeadlineMilliseconds: 30000,
        DefaultMaxModelCalls: 1,
        AllowedCapabilities: ["text.generation", "calendar.read"],
        PermissionScopes: ["calendar:read"],
        RequiredCapabilities: ["text.generation", "calendar.read"],
        DefaultMaxToolCalls: 1,
        DefaultMaxTaskDepth: 2);

    private static ExecutionPlan ModelThenCalendarPlan(TaskDefinition definition) => new(
        "calendar.generate_then_read@1:test",
        1,
        definition.TaskType,
        definition.Version,
        [
            new ExecutionPlanStep(
                "step-1",
                ExecutionStepKind.Model,
                "text.generation",
                [],
                SideEffectClass.None,
                5000,
                ProfileId: "text.generation.small.eval-v1"),
            new ExecutionPlanStep(
                "execute",
                ExecutionStepKind.Tool,
                "calendar.read",
                ["step-1"],
                SideEffectClass.ReadOnly,
                5000)
        ],
        new ExecutionPlanBudget(2, 1, 1, 1, 2, 30000, 4000, 800));

    private static TaskRequest ModelThenCalendarRequest(string taskType, string idempotencyKey) => new(
        taskType,
        JsonSerializer.SerializeToElement(new
        {
            timezone = "Europe/Tirane",
            objective = "Find a suitable project-review slot."
        }),
        ProjectId: "project-1",
        IdempotencyKey: idempotencyKey,
        Constraints: new TaskConstraints(MaxModelCalls: 1, MaxToolCalls: 1, MaxTaskDepth: 2),
        TenantId: "tenant-a",
        ActorId: "test-runner",
        PermissionScopes: ["calendar:read"]);

    private static async Task<IReadOnlyList<ExecutionEvent>> ReadEventsAsync(
        IExecutionStore store,
        Guid executionId)
    {
        var events = new List<ExecutionEvent>();
        await foreach (var executionEvent in store.ReadEventsAsync(
            executionId,
            0,
            TestContext.Current.CancellationToken))
        {
            events.Add(executionEvent);
        }

        return events;
    }

    private sealed class SqliteContextFactory(string databasePath) : IDbContextFactory<IRouteDbContext>
    {
        private readonly DbContextOptions<IRouteDbContext> _options =
            new DbContextOptionsBuilder<IRouteDbContext>()
                .UseSqlite($"Data Source={databasePath}")
                .Options;

        public IRouteDbContext CreateDbContext() => new(_options);
    }

    private sealed class PostgresContextFactory(string connectionString)
        : IDbContextFactory<IRouteDbContext>
    {
        private readonly DbContextOptions<IRouteDbContext> _options =
            new DbContextOptionsBuilder<IRouteDbContext>()
                .UseNpgsql(connectionString)
                .Options;

        public IRouteDbContext CreateDbContext() => new(_options);
    }

    private static GatewayDeployment TestGatewayDeployment(
        string deploymentId,
        string gatewayId,
        int priority) => new(
            deploymentId,
            gatewayId,
            "generic-provider",
            deploymentId,
            "westeurope",
            "EUR",
            "model-v1",
            ["text.generation"],
            ["*"],
            0.95m,
            0.001m,
            1,
            priority);

    private sealed class TestGatewayRegistry(IReadOnlyList<GatewayDeployment> deployments)
        : IGatewayDeploymentRegistry
    {
        public Task<IReadOnlyList<GatewayDeployment>> ListAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(deployments);
        }
    }

    private sealed class TestGatewayClientFactory(IReadOnlyDictionary<string, IModelGateway> gateways)
        : IGatewayDeploymentClientFactory
    {
        public IModelGateway GetClient(GatewayDeployment deployment) => gateways[deployment.DeploymentId];
    }

    private sealed class ThrowingModelGateway : IModelGateway
    {
        public Task<ModelGatewayResult> ExecuteAsync(
            ModelGatewayRequest request,
            CancellationToken cancellationToken) =>
            throw new ModelGatewayException(
                "model_gateway_http_error",
                "The configured model gateway returned HTTP 503.",
                true,
                503,
                failureKind: ModelGatewayFailureKind.Unavailable,
                gatewayId: "external-failure",
                correlationId: request.CorrelationId);
    }

    private sealed class StreamingModelGateway : IModelGateway
    {
        private readonly DeterministicModelGateway _inner = new();

        public string GatewayId => "external-stream";

        public Task<ModelGatewayResult> ExecuteAsync(
            ModelGatewayRequest request,
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException("The orchestrator must consume the streaming contract.");

        public async IAsyncEnumerable<ModelGatewayStreamEvent> StreamAsync(
            ModelGatewayRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            var result = await _inner.ExecuteAsync(request, cancellationToken);
            yield return new ModelGatewayStreamEvent(
                1,
                ModelGatewayStreamEventKind.OutputDelta,
                Delta: "{\"subject\":");
            yield return new ModelGatewayStreamEvent(
                2,
                ModelGatewayStreamEventKind.Usage,
                Usage: result.Usage);
            await Task.Delay(TimeSpan.FromMilliseconds(15), cancellationToken);
            yield return new ModelGatewayStreamEvent(
                3,
                ModelGatewayStreamEventKind.Completed,
                Result: result with
                {
                    GatewayId = "external-stream",
                    Transport = ModelGatewayTransport.Streaming
                });
        }
    }

    private static TaskRouter CreateTaskRouter()
    {
        var matcher = new MeasuredCapabilityMatcher(new BuiltInModelProfileRegistry());
        var escalation = new MeasuredEscalationPolicy();
        var validator = new ExecutionPlanValidator();
        return new TaskRouter(
            new DirectPathSelector(matcher, escalation),
            new BoundedTaskPlanner(matcher, escalation, validator));
    }

    private sealed class FixedTaskRouter(ExecutionPlan plan) : ITaskRouter
    {
        public Task<RoutingResult> RouteAsync(
            TaskRequest request,
            TaskDefinition definition,
            CancellationToken cancellationToken) =>
            Task.FromResult(new RoutingResult(plan, RoutingFor(plan)));
    }

    private sealed class SingleTaskDefinitionRegistry(TaskDefinition definition) : ITaskDefinitionRegistry
    {
        public Task<TaskDefinition?> FindAsync(string taskType, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult<TaskDefinition?>(
                string.Equals(taskType, definition.TaskType, StringComparison.Ordinal)
                    ? definition
                    : null);
        }
    }

    private static RoutingDecision RoutingFor(ExecutionPlan plan) => new(
        "test.v1",
        plan.Steps.Count == 1 ? RoutingPath.Direct : RoutingPath.Workflow,
        "Fixed test route.",
        plan.Steps[^1].Capability,
        plan.Steps[^1].ProfileId,
        plan.Steps[^1].Kind == ExecutionStepKind.Model ? ModelTier.Strong : null,
        0.8m,
        0.9m,
        0.01m,
        100,
        0.01m,
        0.8m,
        plan.Steps.Count > 1,
        plan.Steps.Count > 1 ? 1 : 0,
        false,
        null,
        []);

    private sealed class ThrowIfInvokedModelGateway : IModelGateway
    {
        public int InvocationCount { get; private set; }

        public Task<ModelGatewayResult> ExecuteAsync(
            ModelGatewayRequest request,
            CancellationToken cancellationToken)
        {
            InvocationCount++;
            throw new InvalidOperationException("The invalid plan reached capability execution.");
        }
    }

    private sealed class CountingModelGateway : IModelGateway
    {
        private readonly DeterministicModelGateway _inner = new();

        public int InvocationCount { get; private set; }

        public Task<ModelGatewayResult> ExecuteAsync(
            ModelGatewayRequest request,
            CancellationToken cancellationToken)
        {
            InvocationCount++;
            return _inner.ExecuteAsync(request, cancellationToken);
        }
    }

    private sealed class InterruptOnceModelGateway : IModelGateway
    {
        private readonly DeterministicModelGateway _inner = new();

        public TaskCompletionSource FirstAttemptStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int InvocationCount { get; private set; }

        public async Task<ModelGatewayResult> ExecuteAsync(
            ModelGatewayRequest request,
            CancellationToken cancellationToken)
        {
            InvocationCount++;
            if (InvocationCount == 1)
            {
                FirstAttemptStarted.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }

            return await _inner.ExecuteAsync(request, cancellationToken);
        }
    }

    private sealed class CapturingModelGateway : IModelGateway
    {
        private readonly DeterministicModelGateway _inner = new();

        public ModelGatewayRequest? LastRequest { get; private set; }

        public Task<ModelGatewayResult> ExecuteAsync(
            ModelGatewayRequest request,
            CancellationToken cancellationToken)
        {
            LastRequest = request;
            return _inner.ExecuteAsync(request, cancellationToken);
        }
    }

    private sealed class FixedCostModelGateway(decimal cost) : IModelGateway
    {
        private readonly DeterministicModelGateway _inner = new();

        public async Task<ModelGatewayResult> ExecuteAsync(
            ModelGatewayRequest request,
            CancellationToken cancellationToken)
        {
            var result = await _inner.ExecuteAsync(request, cancellationToken);
            return result with { Usage = result.Usage with { Cost = cost } };
        }
    }

    private sealed class FailCreatedEventOnceExecutionStore(
        InMemoryExecutionStore inner,
        Exception failure)
        : IExecutionStore
    {
        private int _failed;

        public Task<ExecutionSubmission?> FindByIdempotencyKeyAsync(
            string tenantId,
            string key,
            CancellationToken cancellationToken) =>
            inner.FindByIdempotencyKeyAsync(tenantId, key, cancellationToken);

        public Task<ExecutionSnapshot?> GetAsync(
            Guid executionId,
            CancellationToken cancellationToken) =>
            inner.GetAsync(executionId, cancellationToken);

        public Task CreateAsync(
            ExecutionSnapshot execution,
            string? idempotencyKey,
            string? inputFingerprint,
            CancellationToken cancellationToken) =>
            inner.CreateAsync(execution, idempotencyKey, inputFingerprint, cancellationToken);

        public Task UpdateAsync(
            ExecutionSnapshot execution,
            CancellationToken cancellationToken) =>
            inner.UpdateAsync(execution, cancellationToken);

        public Task<ExecutionSnapshot?> TryTransitionAsync(
            Guid executionId,
            ExecutionStatus expectedStatus,
            ExecutionStatus targetStatus,
            DateTimeOffset updatedAt,
            CancellationToken cancellationToken) =>
            inner.TryTransitionAsync(
                executionId,
                expectedStatus,
                targetStatus,
                updatedAt,
                cancellationToken);

        public Task<bool> TryRequestCancellationAsync(
            Guid executionId,
            DateTimeOffset requestedAt,
            CancellationToken cancellationToken) =>
            inner.TryRequestCancellationAsync(executionId, requestedAt, cancellationToken);

        public IAsyncEnumerable<ExecutionEvent> ReadEventsAsync(
            Guid executionId,
            long afterSequence,
            CancellationToken cancellationToken) =>
            inner.ReadEventsAsync(executionId, afterSequence, cancellationToken);

        public Task<ExecutionEvent> AppendEventAsync(
            Guid executionId,
            string eventType,
            DateTimeOffset occurredAt,
            JsonElement data,
            CancellationToken cancellationToken)
        {
            if (eventType == ExecutionEventTypes.Created &&
                Interlocked.CompareExchange(ref _failed, 1, 0) == 0)
            {
                throw failure;
            }

            return inner.AppendEventAsync(
                executionId,
                eventType,
                occurredAt,
                data,
                cancellationToken);
        }
    }
}
