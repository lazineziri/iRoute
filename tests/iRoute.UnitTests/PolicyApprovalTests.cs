using System.Text.Json;
using iRoute.Contracts;
using iRoute.Core;
using iRoute.Infrastructure;
using iRoute.Runtime;
using Microsoft.EntityFrameworkCore;

namespace iRoute.UnitTests;

public sealed class PolicyApprovalTests
{
    [Fact]
    public void ReadOnlyModelOnlyPlanIsAllowedByPlanWidePolicy()
    {
        var definition = new TaskDefinition(
            "model.readonly",
            1,
            "text.generation",
            800,
            0.8m,
            false,
            SideEffectClass.ReadOnly,
            "model.readonly",
            AllowedCapabilities: ["text.generation"],
            RequiredCapabilities: ["text.generation"]);
        var plan = new ExecutionPlan(
            "model.readonly@1:test",
            1,
            definition.TaskType,
            definition.Version,
            [
                new ExecutionPlanStep(
                    "execute",
                    ExecutionStepKind.Model,
                    "text.generation",
                    [],
                    SideEffectClass.None,
                    5000,
                    ProfileId: "text.generation.small.eval-v1")
            ],
            new ExecutionPlanBudget(1, 1, 0, 1, 1, 5000));

        var policy = new TaskPolicyEngine().Evaluate(
            new TaskRequest(definition.TaskType, JsonSerializer.SerializeToElement(new { })),
            definition,
            plan);

        Assert.Equal(PolicyDecisionKind.Allowed, policy.Decision);
        Assert.Equal("text.generation", policy.Capability);
        Assert.Equal(SideEffectClass.None, policy.SideEffectClass);
    }

    [Fact]
    public async Task MissingPermissionScopeDeniesActionAndWritesAuditEvents()
    {
        using var fixture = CreateFixture();

        var result = await fixture.Orchestrator.ExecuteAsync(
            CreateSendRequest(permissionScopes: []),
            TestContext.Current.CancellationToken);

        Assert.Equal(ExecutionStatus.Failed, result.Status);
        Assert.Equal(ErrorCodes.PermissionScopeDenied, Assert.IsType<Problem>(result.Error).Code);
        Assert.Equal(0, fixture.Executor.InvocationCount);
        Assert.Null(await fixture.Approvals.GetAsync(
            result.ExecutionId,
            "execute",
            TestContext.Current.CancellationToken));
        var events = await ReadEventsAsync(fixture.Executions, result.ExecutionId);
        Assert.Contains(events, item => item.Type == ExecutionEventTypes.PolicyEvaluated);
        var denied = Assert.Single(events, item => item.Type == ExecutionEventTypes.CapabilityDenied);
        Assert.Equal(ErrorCodes.PermissionScopeDenied, denied.Data.GetProperty("code").GetString());
        Assert.Equal("requester", denied.Data.GetProperty("actorId").GetString());
        Assert.Equal("tenant-a", denied.Data.GetProperty("tenantId").GetString());
    }

    [Fact]
    public async Task ExternalWriteRequiresExplicitRequestIntent()
    {
        using var fixture = CreateFixture();
        var request = CreateSendRequest(["email:send"]) with
        {
            Constraints = new TaskConstraints(MaxModelCalls: 0, MaxToolCalls: 1)
        };

        var result = await fixture.Orchestrator.ExecuteAsync(
            request,
            TestContext.Current.CancellationToken);

        Assert.Equal(ExecutionStatus.Failed, result.Status);
        Assert.Equal(ErrorCodes.ExternalWriteNotAllowed, Assert.IsType<Problem>(result.Error).Code);
        Assert.Equal(0, fixture.Executor.InvocationCount);
        var events = await ReadEventsAsync(fixture.Executions, result.ExecutionId);
        Assert.Contains(events, item =>
            item.Type == ExecutionEventTypes.CapabilityDenied &&
            item.Data.GetProperty("code").GetString() == ErrorCodes.ExternalWriteNotAllowed);
    }

    [Fact]
    public async Task ExternalWriteRequiresTenantScopedIdempotencyKey()
    {
        using var fixture = CreateFixture();
        var request = CreateSendRequest(["email:send"]) with { IdempotencyKey = null };

        var result = await fixture.Orchestrator.ExecuteAsync(
            request,
            TestContext.Current.CancellationToken);

        Assert.Equal(ExecutionStatus.Failed, result.Status);
        Assert.Equal(
            ErrorCodes.ExternalActionIdempotencyRequired,
            Assert.IsType<Problem>(result.Error).Code);
        Assert.Equal(0, fixture.Executor.InvocationCount);
        Assert.Null(await fixture.Approvals.GetAsync(
            result.ExecutionId,
            "execute",
            TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task CompletedExternalActionReservationIsReusableOnlyForTheSameInput()
    {
        var store = new InMemoryExternalActionStore();
        var now = DateTimeOffset.UtcNow;
        var action = new ExternalActionRecord(
            Guid.CreateVersion7(),
            "tenant-a",
            "execute",
            "email.send",
            "idempotency-reference",
            "input-reference",
            ExternalActionStatus.Running,
            now,
            now);

        var acquired = await store.ReserveAsync(action, TestContext.Current.CancellationToken);
        Assert.Equal(ExternalActionReservationKind.Acquired, acquired.Kind);
        await store.CompleteAsync(
            action.TenantId,
            action.IdempotencyReference,
            new ExternalActionResult(JsonSerializer.SerializeToElement(new { receiptId = "receipt-1" }), []),
            now.AddSeconds(1),
            TestContext.Current.CancellationToken);

        var reused = await store.ReserveAsync(action, TestContext.Current.CancellationToken);
        var conflict = await store.ReserveAsync(
            action with { InputReference = "different-input-reference" },
            TestContext.Current.CancellationToken);

        Assert.Equal(ExternalActionReservationKind.Reused, reused.Kind);
        Assert.Equal("receipt-1", reused.Action.Result?.Output.GetProperty("receiptId").GetString());
        Assert.Equal(ExternalActionReservationKind.Conflict, conflict.Kind);
    }

    [Fact]
    public async Task ApprovalResumesActionAndRepeatedRequestsDoNotDuplicateIt()
    {
        using var fixture = CreateFixture();
        var request = CreateSendRequest(["email:send"]);
        var waiting = await fixture.Orchestrator.ExecuteAsync(
            request,
            TestContext.Current.CancellationToken);

        Assert.Equal(ExecutionStatus.WaitingForApproval, waiting.Status);
        Assert.Equal(0, fixture.Executor.InvocationCount);
        var pending = await fixture.Approvals.GetAsync(
            waiting.ExecutionId,
            "execute",
            TestContext.Current.CancellationToken);
        Assert.Equal(ApprovalStatus.Pending, Assert.IsType<ApprovalRecord>(pending).Status);

        var unauthorized = await Assert.ThrowsAsync<ApprovalSubmissionException>(() =>
            fixture.Orchestrator.SubmitApprovalAsync(
                waiting.ExecutionId,
                new ApprovalDecision("execute", true),
                "tenant-a",
                "unauthorized-approver",
                ["email:send"],
                TestContext.Current.CancellationToken));
        Assert.Equal(ErrorCodes.PermissionScopeDenied, unauthorized.Code);
        Assert.Equal(ExecutionStatus.WaitingForApproval, Assert.IsType<ExecutionSnapshot>(
            await fixture.Executions.GetAsync(waiting.ExecutionId, TestContext.Current.CancellationToken)).Status);

        var approved = await fixture.Orchestrator.SubmitApprovalAsync(
            waiting.ExecutionId,
            new ApprovalDecision("execute", true, "Recipient and content verified."),
            "tenant-a",
            "approver",
            ["email:send", TaskPolicyEngine.ApprovalPermissionScope],
            TestContext.Current.CancellationToken);

        Assert.Equal(ApprovalStatus.Approved, approved.Approval.Status);
        Assert.Equal(ExecutionStatus.Succeeded, approved.Execution.Status);
        Assert.Equal(1, fixture.Executor.InvocationCount);
        var outcome = Assert.IsType<TaskOutcome>(approved.Execution.Outcome);
        Assert.Equal(ResolutionLevel.DeterministicCapability, outcome.ResolutionLevel);
        Assert.Equal(1, outcome.Usage.ToolCalls);

        var replayedDecision = await fixture.Orchestrator.SubmitApprovalAsync(
            waiting.ExecutionId,
            new ApprovalDecision("execute", true),
            "tenant-a",
            "approver",
            ["email:send", TaskPolicyEngine.ApprovalPermissionScope],
            TestContext.Current.CancellationToken);
        var replayedRequest = await fixture.Orchestrator.ExecuteAsync(
            request,
            TestContext.Current.CancellationToken);

        Assert.Equal(approved.Execution.ExecutionId, replayedDecision.Execution.ExecutionId);
        Assert.Equal(approved.Execution.ExecutionId, replayedRequest.ExecutionId);
        Assert.Equal(1, fixture.Executor.InvocationCount);
        var events = await ReadEventsAsync(fixture.Executions, waiting.ExecutionId);
        Assert.Single(events, item => item.Type == ExecutionEventTypes.ApprovalRequired);
        Assert.Single(events, item => item.Type == ExecutionEventTypes.ApprovalDecided);
        Assert.Single(events, item => item.Type == ExecutionEventTypes.ExternalActionStarted);
        Assert.Single(events, item => item.Type == ExecutionEventTypes.ExternalActionCompleted);
        Assert.Contains(events, item => item.Type == ExecutionEventTypes.CapabilityDenied);
        Assert.DoesNotContain(events, item => item.Data.GetRawText().Contains("investor@example.com", StringComparison.Ordinal));
    }

    [Fact]
    public async Task CancellationRacingWithAConfirmedExternalSuccessPersistsTheResult()
    {
        var executions = new InMemoryExecutionStore();
        var artifacts = new InMemoryArtifactStore();
        var checkpoints = new InMemoryWorkflowCheckpointStore();
        var approvals = new InMemoryApprovalStore();
        var actions = new CapturingExternalActionStore(new InMemoryExternalActionStore());
        using var cancellations = new ExecutionCancellationRegistry();
        using var callerCancellation = new CancellationTokenSource();
        var executor = new CancellingAfterSuccessExecutor(callerCancellation);
        var orchestrator = CreateOrchestrator(
            executions,
            artifacts,
            checkpoints,
            approvals,
            actions,
            cancellations,
            executor);
        var request = CreateSendRequest(["email:send"]);
        var waiting = await orchestrator.ExecuteAsync(
            request,
            TestContext.Current.CancellationToken);

        var approved = await orchestrator.SubmitApprovalAsync(
            waiting.ExecutionId,
            new ApprovalDecision("execute", true),
            "tenant-a",
            "approver",
            ["email:send", TaskPolicyEngine.ApprovalPermissionScope],
            callerCancellation.Token);

        Assert.Equal(ExecutionStatus.Cancelled, approved.Execution.Status);
        Assert.Equal(1, executor.InvocationCount);
        Assert.Equal(ExternalActionStatus.Succeeded, actions.CompletedAction?.Status);
        Assert.Equal(
            "accepted",
            actions.CompletedAction?.Result?.Output.GetProperty("status").GetString());
    }

    [Fact]
    public async Task MultiStepReadOnlyPlanCreatesApprovalForFinalAction()
    {
        var definition = new TaskDefinition(
            "calendar.compare_with_database",
            1,
            "database.read",
            800,
            0.8m,
            true,
            SideEffectClass.ReadOnly,
            "calendar.database-comparison",
            DefaultMaxModelCalls: 0,
            AllowedCapabilities: ["calendar.read", "database.read"],
            PermissionScopes: ["calendar:read", "database:read"],
            ApprovalRequired: true,
            RequiredCapabilities: ["calendar.read", "database.read"],
            DefaultMaxToolCalls: 2,
            DefaultMaxTaskDepth: 2);
        var plan = new ExecutionPlan(
            "calendar.compare_with_database@1:test",
            1,
            definition.TaskType,
            definition.Version,
            [
                new ExecutionPlanStep(
                    "step-1",
                    ExecutionStepKind.Tool,
                    "calendar.read",
                    [],
                    SideEffectClass.ReadOnly,
                    5000),
                new ExecutionPlanStep(
                    "execute",
                    ExecutionStepKind.Tool,
                    "database.read",
                    ["step-1"],
                    SideEffectClass.ReadOnly,
                    5000)
            ],
            new ExecutionPlanBudget(2, 0, 2, 1, 2, 10000));
        using var fixture = CreateFixture(
            new FixedTaskRouter(plan),
            new SingleTaskDefinitionRegistry(definition));

        var waiting = await fixture.Orchestrator.ExecuteAsync(
            new TaskRequest(
                definition.TaskType,
                JsonSerializer.SerializeToElement(new
                {
                    timezone = "Europe/Tirane",
                    queryId = "project-status"
                }),
                ProjectId: "project-1",
                IdempotencyKey: "multi-step-approval",
                Constraints: new TaskConstraints(MaxModelCalls: 0, MaxToolCalls: 2, MaxTaskDepth: 2),
                TenantId: "tenant-a",
                ActorId: "requester",
                PermissionScopes: ["calendar:read", "database:read"]),
            TestContext.Current.CancellationToken);

        Assert.Equal(ExecutionStatus.WaitingForApproval, waiting.Status);
        var approval = Assert.IsType<ApprovalRecord>(await fixture.Approvals.GetAsync(
            waiting.ExecutionId,
            "execute",
            TestContext.Current.CancellationToken));
        Assert.Equal("database.read", approval.Capability);
        Assert.Equal(SideEffectClass.ReadOnly, approval.SideEffectClass);
    }

    [Fact]
    public async Task ConcurrentInlineApprovalReplayClaimsExecutionOnlyOnce()
    {
        var executions = new InMemoryExecutionStore();
        var artifacts = new InMemoryArtifactStore();
        var checkpoints = new InMemoryWorkflowCheckpointStore();
        var approvalStore = new InMemoryApprovalStore();
        var actions = new InMemoryExternalActionStore();
        var executor = new BlockingExternalActionExecutor();
        using var policy = new CoordinatedApprovedPolicyEngine();
        using var cancellations = new ExecutionCancellationRegistry();
        var orchestrator = CreateOrchestrator(
            executions,
            artifacts,
            checkpoints,
            approvalStore,
            actions,
            cancellations,
            executor,
            policyEngine: policy);
        var waiting = await orchestrator.ExecuteAsync(
            CreateSendRequest(["email:send"]),
            TestContext.Current.CancellationToken);
        var decision = new ApprovalDecision("execute", true, "Concurrent replay test.");

        var first = Task.Run(() => orchestrator.SubmitApprovalAsync(
            waiting.ExecutionId,
            decision,
            "tenant-a",
            "approver",
            ["email:send", TaskPolicyEngine.ApprovalPermissionScope],
            TestContext.Current.CancellationToken));
        var second = Task.Run(() => orchestrator.SubmitApprovalAsync(
            waiting.ExecutionId,
            decision,
            "tenant-a",
            "approver",
            ["email:send", TaskPolicyEngine.ApprovalPermissionScope],
            TestContext.Current.CancellationToken));
        await executor.Started.Task.WaitAsync(
            TimeSpan.FromSeconds(10),
            TestContext.Current.CancellationToken);
        executor.Release.TrySetResult();
        var results = await Task.WhenAll(first, second);

        Assert.All(results, result => Assert.Equal(waiting.ExecutionId, result.Execution.ExecutionId));
        Assert.Equal(1, executor.InvocationCount);
        var final = Assert.IsType<ExecutionSnapshot>(await executions.GetAsync(
            waiting.ExecutionId,
            TestContext.Current.CancellationToken));
        Assert.Equal(ExecutionStatus.Succeeded, final.Status);
        var events = await ReadEventsAsync(executions, waiting.ExecutionId);
        Assert.Single(events, item => item.Type == ExecutionEventTypes.ExternalActionCompleted);
        Assert.Single(events, item => item.Type == ExecutionEventTypes.ArtifactMaterialized);
        Assert.Single(events, item => item.Type == ExecutionEventTypes.Completed);
    }

    [Fact]
    public async Task ApprovedActionIsQueuedAndOnlyOneWorkerPerformsTheSideEffect()
    {
        var executions = new InMemoryExecutionStore();
        var artifacts = new InMemoryArtifactStore();
        var checkpoints = new InMemoryWorkflowCheckpointStore();
        var approvals = new InMemoryApprovalStore();
        var actions = new InMemoryExternalActionStore();
        var executor = new CountingExternalActionExecutor();
        var work = new InMemoryExecutionWorkStore(executions);
        using var cancellations = new ExecutionCancellationRegistry();
        var orchestrator = CreateOrchestrator(
            executions,
            artifacts,
            checkpoints,
            approvals,
            actions,
            cancellations,
            executor,
            executionWork: work);
        var waiting = await orchestrator.SubmitAsync(
            CreateSendRequest(["email:send"]),
            TestContext.Current.CancellationToken);

        var approved = await orchestrator.SubmitApprovalForQueueAsync(
            waiting.ExecutionId,
            new ApprovalDecision("execute", true, "Approved for durable execution."),
            "tenant-a",
            "approver",
            ["email:send", TaskPolicyEngine.ApprovalPermissionScope],
            TestContext.Current.CancellationToken);

        Assert.Equal(ExecutionStatus.Queued, approved.Execution.Status);
        Assert.Equal(0, executor.InvocationCount);
        var claims = await Task.WhenAll(
            work.TryClaimAsync(
                "action-worker-a",
                DateTimeOffset.UtcNow,
                TimeSpan.FromSeconds(30),
                TestContext.Current.CancellationToken),
            work.TryClaimAsync(
                "action-worker-b",
                DateTimeOffset.UtcNow,
                TimeSpan.FromSeconds(30),
                TestContext.Current.CancellationToken));
        var lease = Assert.IsType<ExecutionLease>(Assert.Single(claims, claim => claim is not null));
        var completed = await orchestrator.ProcessQueuedAsync(
            waiting.ExecutionId,
            TestContext.Current.CancellationToken);
        Assert.True(await work.CompleteAsync(
            lease,
            DateTimeOffset.UtcNow,
            TestContext.Current.CancellationToken));
        var replayed = await orchestrator.ProcessQueuedAsync(
            waiting.ExecutionId,
            TestContext.Current.CancellationToken);

        Assert.Equal(ExecutionStatus.Succeeded, completed.Status);
        Assert.Equal(completed, replayed);
        Assert.Equal(1, executor.InvocationCount);
        Assert.Single(
            await ReadEventsAsync(executions, waiting.ExecutionId),
            item => item.Type == ExecutionEventTypes.ExternalActionCompleted);
    }

    [Fact]
    public async Task DeniedApprovalTerminatesWithoutExecutingAction()
    {
        using var fixture = CreateFixture();
        var waiting = await fixture.Orchestrator.ExecuteAsync(
            CreateSendRequest(["email:send"]),
            TestContext.Current.CancellationToken);

        var denied = await fixture.Orchestrator.SubmitApprovalAsync(
            waiting.ExecutionId,
            new ApprovalDecision("execute", false, "Recipient is incorrect."),
            "tenant-a",
            "approver",
            ["email:send", TaskPolicyEngine.ApprovalPermissionScope],
            TestContext.Current.CancellationToken);

        Assert.Equal(ApprovalStatus.Denied, denied.Approval.Status);
        Assert.Equal(ExecutionStatus.Failed, denied.Execution.Status);
        Assert.Equal(ErrorCodes.ApprovalDenied, Assert.IsType<Problem>(denied.Execution.Error).Code);
        Assert.Equal(0, fixture.Executor.InvocationCount);
        var events = await ReadEventsAsync(fixture.Executions, waiting.ExecutionId);
        Assert.Single(events, item => item.Type == ExecutionEventTypes.ApprovalDecided);
        Assert.DoesNotContain(events, item => item.Type == ExecutionEventTypes.ExternalActionStarted);
    }

    [Fact]
    public async Task CapabilityOutsideTaskAllowListIsDeniedAndAudited()
    {
        var plan = new ExecutionPlan(
            "email.send@1:untrusted",
            1,
            "email.send",
            1,
            [
                new ExecutionPlanStep(
                    "execute",
                    ExecutionStepKind.Tool,
                    "email.delete",
                    [],
                    SideEffectClass.IrreversibleWrite,
                    30_000)
            ],
            new ExecutionPlanBudget(1, 0, 1, 1, 1, 30_000));
        using var fixture = CreateFixture(taskRouter: new FixedTaskRouter(plan));

        var result = await fixture.Orchestrator.ExecuteAsync(
            CreateSendRequest(["email:send"]),
            TestContext.Current.CancellationToken);

        Assert.Equal(ExecutionStatus.Failed, result.Status);
        Assert.Equal(ErrorCodes.CapabilityNotAllowed, Assert.IsType<Problem>(result.Error).Code);
        Assert.Equal(0, fixture.Executor.InvocationCount);
        var events = await ReadEventsAsync(fixture.Executions, result.ExecutionId);
        Assert.Contains(events, item =>
            item.Type == ExecutionEventTypes.CapabilityDenied &&
            item.Data.GetProperty("capability").GetString() == "email.delete");
    }

    [Fact]
    public async Task SqliteApprovalCanResumeAfterRuntimeRestartWithoutDuplicateAction()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"iroute-policy-{Guid.NewGuid():N}.db");
        try
        {
            var contextFactory = new SqliteContextFactory(databasePath);
            await using (var context = contextFactory.CreateDbContext())
            {
                await context.Database.MigrateAsync(TestContext.Current.CancellationToken);
                var migrations = await context.Database.GetAppliedMigrationsAsync(TestContext.Current.CancellationToken);
                Assert.Contains(iRoute.Infrastructure.Migrations.PolicyApprovals.MigrationId, migrations);
            }

            var executor = new CountingExternalActionExecutor();
            ExecutionSnapshot waiting;
            using (var cancellations = new ExecutionCancellationRegistry())
            {
                waiting = await CreateDurableOrchestrator(contextFactory, cancellations, executor)
                    .ExecuteAsync(
                        CreateSendRequest(["email:send"]),
                        TestContext.Current.CancellationToken);
            }

            using (var restartedCancellations = new ExecutionCancellationRegistry())
            {
                var restarted = CreateDurableOrchestrator(contextFactory, restartedCancellations, executor);
                var approved = await restarted.SubmitApprovalAsync(
                    waiting.ExecutionId,
                    new ApprovalDecision("execute", true),
                    "tenant-a",
                    "approver",
                    ["email:send", TaskPolicyEngine.ApprovalPermissionScope],
                    TestContext.Current.CancellationToken);
                Assert.Equal(ExecutionStatus.Succeeded, approved.Execution.Status);
            }

            using (var replayCancellations = new ExecutionCancellationRegistry())
            {
                var replayed = await CreateDurableOrchestrator(contextFactory, replayCancellations, executor)
                    .SubmitApprovalAsync(
                        waiting.ExecutionId,
                        new ApprovalDecision("execute", true),
                        "tenant-a",
                        "approver",
                        ["email:send", TaskPolicyEngine.ApprovalPermissionScope],
                        TestContext.Current.CancellationToken);
                Assert.Equal(ExecutionStatus.Succeeded, replayed.Execution.Status);
            }

            Assert.Equal(1, executor.InvocationCount);
            var approval = await new EfApprovalStore(contextFactory).GetAsync(
                waiting.ExecutionId,
                "execute",
                TestContext.Current.CancellationToken);
            Assert.Equal(ApprovalStatus.Approved, Assert.IsType<ApprovalRecord>(approval).Status);
        }
        finally
        {
            File.Delete(databasePath);
        }
    }

    private static Fixture CreateFixture(
        ITaskRouter? taskRouter = null,
        ITaskDefinitionRegistry? taskDefinitions = null)
    {
        var executions = new InMemoryExecutionStore();
        var artifacts = new InMemoryArtifactStore();
        var checkpoints = new InMemoryWorkflowCheckpointStore();
        var approvals = new InMemoryApprovalStore();
        var actions = new InMemoryExternalActionStore();
        var executor = new CountingExternalActionExecutor();
        var cancellations = new ExecutionCancellationRegistry();
        return new Fixture(
            CreateOrchestrator(
                executions,
                artifacts,
                checkpoints,
                approvals,
                actions,
                cancellations,
                executor,
                taskRouter,
                taskDefinitions: taskDefinitions),
            executions,
            approvals,
            executor,
            cancellations);
    }

    private static ExecutionOrchestrator CreateDurableOrchestrator(
        IDbContextFactory<IRouteDbContext> contextFactory,
        ExecutionCancellationRegistry cancellations,
        IExternalActionExecutor executor) =>
        CreateOrchestrator(
            new EfExecutionStore(contextFactory, new NullExecutionFence()),
            new EfArtifactStore(contextFactory),
            new EfWorkflowCheckpointStore(contextFactory),
            new EfApprovalStore(contextFactory),
            new EfExternalActionStore(contextFactory),
            cancellations,
            executor);

    private static ExecutionOrchestrator CreateOrchestrator(
        IExecutionStore executions,
        IArtifactStore artifacts,
        IWorkflowCheckpointStore checkpoints,
        IApprovalStore approvals,
        IExternalActionStore actions,
        ExecutionCancellationRegistry cancellations,
        IExternalActionExecutor executor,
        ITaskRouter? taskRouter = null,
        IExecutionWorkStore? executionWork = null,
        ITaskDefinitionRegistry? taskDefinitions = null,
        ITaskPolicyEngine? policyEngine = null)
    {
        var definitions = taskDefinitions ?? new BuiltInTaskDefinitionRegistry();
        var fingerprint = new Sha256InputFingerprint();
        var clock = new SystemClock();
        var memories = new InMemoryMemoryStore();
        return new ExecutionOrchestrator(
            executions,
            artifacts,
            new ProjectMemoryMaterializer(memories, artifacts, clock),
            [
                new ExactResultResolver(artifacts, fingerprint, clock),
                new FactDecisionResolver(memories, clock),
                new ArtifactLookupResolver(artifacts, clock),
                new DeterministicHandlerResolver([], clock)
            ],
            definitions,
            taskRouter ?? CreateTaskRouter(),
            new ExecutionPlanValidator(),
            policyEngine ?? new TaskPolicyEngine(),
            checkpoints,
            approvals,
            actions,
            new BoundedDependencyScheduler(
                checkpoints,
                executions,
                clock,
                new WorkflowSchedulerOptions()),
            CreateCapabilityExecutor(clock),
            new DeterministicModelGateway(),
            executor,
            new BoundedContextCompiler(memories, artifacts, clock),
            [
                new EmailDraftOutcomeValidator(),
                new EmailSendOutcomeValidator(),
                new DefaultTaskOutcomeValidator()
            ],
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

    private static TaskRequest CreateSendRequest(IReadOnlyList<string> permissionScopes) => new(
        "email.send",
        JsonSerializer.SerializeToElement(new
        {
            recipient = "investor@example.com",
            subject = "iRoute update",
            body = "The deterministic kernel is ready."
        }),
        ProjectId: "project-1",
        IdempotencyKey: "send-investor-update",
        Constraints: new TaskConstraints(
            AllowExternalWrites: true,
            MaxModelCalls: 0,
            MaxToolCalls: 1),
        TenantId: "tenant-a",
        ActorId: "requester",
        PermissionScopes: permissionScopes);

    private static async Task<IReadOnlyList<ExecutionEvent>> ReadEventsAsync(
        IExecutionStore store,
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

    private sealed record Fixture(
        ExecutionOrchestrator Orchestrator,
        IExecutionStore Executions,
        IApprovalStore Approvals,
        CountingExternalActionExecutor Executor,
        ExecutionCancellationRegistry Cancellations) : IDisposable
    {
        public void Dispose() => Cancellations.Dispose();
    }

    private sealed class CountingExternalActionExecutor : IExternalActionExecutor
    {
        public int InvocationCount { get; private set; }

        public Task<ExternalActionResult> ExecuteAsync(
            ExternalActionRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            InvocationCount++;
            return Task.FromResult(new ExternalActionResult(
                JsonSerializer.SerializeToElement(new
                {
                    receiptId = $"receipt-{request.IdempotencyKey[..16]}",
                    status = "accepted",
                    capability = request.Capability
                }),
                [new EvidenceReference("external-action", $"receipt:{request.IdempotencyKey}")]));
        }
    }

    private sealed class BlockingExternalActionExecutor : IExternalActionExecutor
    {
        private int _invocationCount;

        public TaskCompletionSource Started { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int InvocationCount => Volatile.Read(ref _invocationCount);

        public async Task<ExternalActionResult> ExecuteAsync(
            ExternalActionRequest request,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _invocationCount);
            Started.TrySetResult();
            await Release.Task.WaitAsync(cancellationToken);
            return new ExternalActionResult(
                JsonSerializer.SerializeToElement(new
                {
                    receiptId = "concurrent-approval-receipt",
                    status = "accepted",
                    capability = request.Capability
                }),
                [new EvidenceReference("external-action", "receipt:concurrent-approval")]);
        }
    }

    private sealed class CancellingAfterSuccessExecutor(CancellationTokenSource cancellation)
        : IExternalActionExecutor
    {
        private int _invocationCount;
        public int InvocationCount => Volatile.Read(ref _invocationCount);

        public Task<ExternalActionResult> ExecuteAsync(
            ExternalActionRequest request,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _invocationCount);
            var result = new ExternalActionResult(
                JsonSerializer.SerializeToElement(new
                {
                    receiptId = "confirmed-before-cancellation",
                    status = "accepted",
                    capability = request.Capability
                }),
                [new EvidenceReference("external-action", "receipt:confirmed-before-cancellation")]);
            cancellation.Cancel();
            return Task.FromResult(result);
        }
    }

    private sealed class CapturingExternalActionStore(IExternalActionStore inner)
        : IExternalActionStore
    {
        public ExternalActionRecord? CompletedAction { get; private set; }

        public Task<ExternalActionReservation> ReserveAsync(
            ExternalActionRecord action,
            CancellationToken cancellationToken) =>
            inner.ReserveAsync(action, cancellationToken);

        public async Task<ExternalActionRecord> CompleteAsync(
            string tenantId,
            string idempotencyReference,
            ExternalActionResult result,
            DateTimeOffset completedAt,
            CancellationToken cancellationToken)
        {
            CompletedAction = await inner.CompleteAsync(
                tenantId,
                idempotencyReference,
                result,
                completedAt,
                cancellationToken);
            return CompletedAction;
        }

        public Task<ExternalActionRecord> FailAsync(
            string tenantId,
            string idempotencyReference,
            Problem problem,
            DateTimeOffset failedAt,
            CancellationToken cancellationToken) =>
            inner.FailAsync(tenantId, idempotencyReference, problem, failedAt, cancellationToken);

        public Task<IReadOnlyList<ExternalActionRecord>> ListUnresolvedAsync(
            string tenantId,
            Guid executionId,
            CancellationToken cancellationToken) =>
            inner.ListUnresolvedAsync(tenantId, executionId, cancellationToken);
    }

    private sealed class CoordinatedApprovedPolicyEngine : ITaskPolicyEngine, IDisposable
    {
        private readonly TaskPolicyEngine _inner = new();
        private readonly ManualResetEventSlim _bothApprovedEvaluations = new(false);
        private int _approvedEvaluations;

        public PolicyEvaluation Evaluate(
            TaskRequest request,
            TaskDefinition definition,
            ExecutionPlan plan,
            ApprovalRecord? approval = null)
        {
            var result = _inner.Evaluate(request, definition, plan, approval);
            if (approval?.Status == ApprovalStatus.Approved)
            {
                if (Interlocked.Increment(ref _approvedEvaluations) == 2)
                {
                    _bothApprovedEvaluations.Set();
                }

                if (!_bothApprovedEvaluations.Wait(TimeSpan.FromSeconds(10)))
                {
                    throw new TimeoutException("Both approval replays did not reach policy evaluation.");
                }
            }

            return result;
        }

        public PolicyEvaluation EvaluateApproval(
            ApprovalRecord approval,
            IReadOnlyCollection<string> approverPermissionScopes) =>
            _inner.EvaluateApproval(approval, approverPermissionScopes);

        public void Dispose() => _bothApprovedEvaluations.Dispose();
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

    private sealed class SingleTaskDefinitionRegistry(TaskDefinition definition)
        : ITaskDefinitionRegistry
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

    private sealed class SqliteContextFactory(string databasePath) : IDbContextFactory<IRouteDbContext>
    {
        private readonly DbContextOptions<IRouteDbContext> _options =
            new DbContextOptionsBuilder<IRouteDbContext>()
                .UseSqlite($"Data Source={databasePath}")
                .Options;

        public IRouteDbContext CreateDbContext() => new(_options);
    }
}
