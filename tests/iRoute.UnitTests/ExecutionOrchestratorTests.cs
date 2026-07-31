using System.Text.Json;
using iRoute.Contracts;
using iRoute.Core;
using iRoute.Infrastructure;
using iRoute.Runtime;
using Microsoft.EntityFrameworkCore;

namespace iRoute.UnitTests;

public sealed class ExecutionOrchestratorTests
{
    private static readonly string[] ActiveDecisions =
        ["Use the Markdown specification as the source of truth."];
    private static readonly string[] ChangedDecisions =
        ["Use PostgreSQL as the production source of truth."];

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
        Assert.Equal(ResolutionLevel.StrongModel, outcome.ResolutionLevel);
        Assert.Equal(1, outcome.Usage.ModelCalls);
        Assert.True(Assert.IsType<ValidationSummary>(outcome.Validation).Passed);
        Assert.False(Assert.IsType<ContextManifest>(outcome.Context).Truncated);
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
        Assert.Contains(events, x => x.Type == ExecutionEventTypes.WorkflowCheckpointed);
        Assert.Contains(events, x => x.Type == ExecutionEventTypes.StepStarted);
        Assert.Contains(events, x => x.Type == ExecutionEventTypes.StepCompleted);
        Assert.Contains(events, x => x.Type == ExecutionEventTypes.ContextCompiled);
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
        Assert.Equal(ResolutionLevel.StrongModel, secondOutcome.ResolutionLevel);
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
    public async Task QualityBelowRequestedFloorFailsClosed()
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
        Assert.Equal("validation_failed", Assert.IsType<Problem>(result.Error).Code);
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
        Assert.Equal("503", Assert.IsType<Dictionary<string, string>>(problem.Metadata)["gatewayStatusCode"]);
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
            new FixedExecutionPlanFactory(invalidPlan));

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
                    new EfExecutionStore(factory),
                    new EfArtifactStore(factory),
                    cancellations,
                    checkpoints: new EfWorkflowCheckpointStore(factory),
                    memories: new EfMemoryStore(factory));
                first = await orchestrator.ExecuteAsync(
                    CreateRequest("tenant-a", "persistent-first"),
                    TestContext.Current.CancellationToken);
            }

            var restartedStore = new EfExecutionStore(factory);
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

            Assert.Equal(ExecutionStatus.Succeeded, first.Status);
            Assert.Equal(ResolutionLevel.ExactArtifact, Assert.IsType<TaskOutcome>(second.Outcome).ResolutionLevel);
            Assert.Equal(
                ResolutionLevel.StructuredState,
                Assert.IsType<TaskOutcome>(decision.Outcome).ResolutionLevel);
            Assert.Equal(0, Assert.IsType<TaskOutcome>(decision.Outcome).Usage.ModelCalls);
            Assert.NotEmpty(await ReadEventsAsync(restartedStore, first.ExecutionId));
        }
        finally
        {
            File.Delete(databasePath);
        }
    }

    private static ExecutionOrchestrator CreateOrchestrator(
        IExecutionStore store,
        IArtifactStore artifacts,
        ExecutionCancellationRegistry cancellations,
        IModelGateway? modelGateway = null,
        IExecutionPlanFactory? planFactory = null,
        IWorkflowCheckpointStore? checkpoints = null,
        IApprovalStore? approvals = null,
        IExternalActionStore? externalActions = null,
        IExternalActionExecutor? externalActionExecutor = null,
        IMemoryStore? memories = null,
        IEnumerable<IDeterministicTaskHandler>? deterministicHandlers = null)
    {
        var definitions = new BuiltInTaskDefinitionRegistry();
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
            planFactory ?? new DirectExecutionPlanFactory(),
            new ExecutionPlanValidator(),
            new TaskPolicyEngine(),
            checkpoints,
            approvals,
            externalActions,
            scheduler,
            modelGateway ?? new DeterministicModelGateway(),
            externalActionExecutor ?? new DevelopmentExternalActionExecutor(),
            new BoundedContextCompiler(),
            [new EmailDraftOutcomeValidator(), new DefaultTaskOutcomeValidator()],
            fingerprint,
            cancellations,
            clock);
    }

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

    private sealed class ThrowingModelGateway : IModelGateway
    {
        public Task<ModelGatewayResult> ExecuteAsync(
            ModelGatewayRequest request,
            CancellationToken cancellationToken) =>
            throw new ModelGatewayException(
                "model_gateway_http_error",
                "The configured model gateway returned HTTP 503.",
                true,
                503);
    }

    private sealed class FixedExecutionPlanFactory(ExecutionPlan plan) : IExecutionPlanFactory
    {
        public ExecutionPlan Create(TaskRequest request, TaskDefinition definition) => plan;
    }

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
}
