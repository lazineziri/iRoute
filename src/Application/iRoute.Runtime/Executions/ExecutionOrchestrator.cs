using System.Diagnostics;
using System.Text.Json;
using iRoute.Contracts;
using iRoute.Core;

namespace iRoute.Runtime;

public sealed class ExecutionOrchestrator(
    IExecutionStore store,
    IArtifactStore artifacts,
    ProjectMemoryMaterializer projectMemory,
    IEnumerable<INoModelResolver> resolvers,
    ITaskDefinitionRegistry taskDefinitions,
    ITaskRouter taskRouter,
    IExecutionPlanValidator planValidator,
    ITaskPolicyEngine policyEngine,
    IWorkflowCheckpointStore checkpoints,
    IApprovalStore approvals,
    IExternalActionStore externalActions,
    BoundedDependencyScheduler scheduler,
    ICapabilityExecutor capabilityExecutor,
    IModelGateway modelGateway,
    IExternalActionExecutor externalActionExecutor,
    IContextCompiler contextCompiler,
    IEnumerable<ITaskOutcomeValidator> validators,
    IInputFingerprint fingerprint,
    IExecutionCancellationRegistry cancellations,
    IClock clock,
    IExecutionTelemetry? executionTelemetry = null,
    IExecutionWorkStore? executionWork = null)
{
    private static readonly JsonSerializerOptions ContractJsonOptions = new(JsonSerializerDefaults.Web);
    private readonly IExecutionTelemetry _telemetry = executionTelemetry ?? NoOpExecutionTelemetry.Instance;

    public Task<ExecutionSnapshot> ExecuteAsync(TaskRequest request, CancellationToken cancellationToken) =>
        ExecuteCoreAsync(request, false, cancellationToken);

    public Task<ExecutionSnapshot> SubmitAsync(TaskRequest request, CancellationToken cancellationToken) =>
        ExecuteCoreAsync(request, true, cancellationToken);

    private async Task<ExecutionSnapshot> ExecuteCoreAsync(
        TaskRequest request,
        bool deferExecution,
        CancellationToken cancellationToken)
    {
        Validate(request);
        var tenantId = RequestScope.Tenant(request);
        var actorId = RequestScope.Actor(request);

        var submissionFingerprint = string.IsNullOrWhiteSpace(request.IdempotencyKey)
            ? null
            : fingerprint.CreateForSubmission(request);

        if (!string.IsNullOrWhiteSpace(request.IdempotencyKey))
        {
            var existing = await store.FindByIdempotencyKeyAsync(
                tenantId,
                request.IdempotencyKey,
                cancellationToken);
            if (existing is not null)
            {
                return ReplayOrConflict(existing, request.IdempotencyKey, submissionFingerprint);
            }
        }

        var now = clock.UtcNow;
        var snapshot = new ExecutionSnapshot(
            Guid.CreateVersion7(),
            request.TaskType,
            ExecutionStatus.Accepted,
            now,
            now,
            TenantId: tenantId,
            ActorId: actorId,
            ProjectId: request.ProjectId);
        using var trace = _telemetry.StartExecution(
            snapshot,
            request.PermissionScopes ?? [],
            "execute");
        try
        {
            await store.CreateAsync(
                snapshot,
                request.IdempotencyKey,
                submissionFingerprint,
                cancellationToken);
        }
        catch (IdempotencyConflictException) when (!string.IsNullOrWhiteSpace(request.IdempotencyKey))
        {
            // A concurrent submit carrying the same key won the insert. Answer with the execution
            // that was actually created rather than failing the retry this key exists to support.
            var winner = await store.FindByIdempotencyKeyAsync(
                tenantId,
                request.IdempotencyKey,
                cancellationToken)
                ?? throw new IdempotencyKeyReusedException(request.IdempotencyKey);

            return ReplayOrConflict(winner, request.IdempotencyKey, submissionFingerprint);
        }
        var registeredCancellation = default(CancellationToken);
        CancellationTokenSource? deadlineSource = null;
        CancellationTokenSource? executionSource = null;
        var cancellationRegistered = false;
        try
        {
            await AppendEventAsync(
                snapshot.ExecutionId,
                ExecutionEventTypes.Created,
                new
                {
                    snapshot.TaskType,
                    snapshot.TenantId,
                    snapshot.ActorId,
                    snapshot.ProjectId,
                    traceId = trace.TraceId
                },
                cancellationToken);

            registeredCancellation = cancellations.Register(snapshot.ExecutionId, cancellationToken);
            cancellationRegistered = true;
            deadlineSource = new CancellationTokenSource();
            var requestedDeadline = request.Constraints?.DeadlineMilliseconds ?? 30000;
            deadlineSource.CancelAfter(TimeSpan.FromMilliseconds(requestedDeadline));
            executionSource = CancellationTokenSource.CreateLinkedTokenSource(
                registeredCancellation,
                deadlineSource.Token);
            var executionToken = executionSource.Token;

            var definition = await taskDefinitions.FindAsync(request.TaskType, executionToken)
                ?? throw new TaskExecutionException(
                    ErrorCodes.UnknownTaskType,
                    "Unknown task type",
                    $"No active task definition exists for '{request.TaskType}'.");
            snapshot = snapshot with { TaskDefinitionVersion = definition.Version, UpdatedAt = clock.UtcNow };
            await store.UpdateAsync(snapshot, executionToken);

            snapshot = await TransitionAsync(snapshot, ExecutionStatus.Resolving, executionToken);
            if (definition.SideEffectClass >= SideEffectClass.ReversibleWrite)
            {
                foreach (var resolver in resolvers.OrderBy(item => item.Order))
                {
                    await AppendResolutionDecisionAsync(
                        snapshot.ExecutionId,
                        resolver.Name,
                        new ResolutionDecision(
                            false,
                            ResolutionDecisionCodes.ExternalWriteBlocked,
                            "External-write outcomes cannot bypass current permission and approval policy.",
                            false,
                            false,
                            ["The task side-effect class was checked before state lookup."]),
                        executionToken);
                }
            }
            else
            {
                foreach (var resolver in resolvers.OrderBy(x => x.Order))
                {
                    var decision = await resolver.ResolveAsync(request, definition, executionToken);
                    var candidate = decision.Candidate;
                    OutcomeValidationResult? validation = null;
                    if (decision.Accepted && candidate is not null)
                    {
                        var validator = validators.First(item => item.Supports(request.TaskType));
                        validation = await validator.ValidateAsync(
                            request,
                            definition,
                            new ModelGatewayResult(
                                candidate.Output,
                                candidate.Usage ?? new UsageSummary(),
                                candidate.Confidence,
                                candidate.Evidence),
                            EmptyCompiledContext(),
                            executionToken);
                        if (!validation.Passed)
                        {
                            decision = decision with
                            {
                                Accepted = false,
                                Code = ResolutionDecisionCodes.ValidationFailed,
                                Reason = $"The reusable result failed task validation: {string.Join(" ", validation.Failures)}",
                                Checks = decision.Checks.Concat(validation.Checks).ToArray(),
                                Candidate = null
                            };
                            candidate = null;
                        }
                    }

                    await AppendResolutionDecisionAsync(
                        snapshot.ExecutionId,
                        resolver.Name,
                        decision,
                        executionToken);
                    if (!decision.Accepted || candidate is null || validation is null)
                    {
                        continue;
                    }

                    snapshot = await TransitionAsync(snapshot, ExecutionStatus.Validating, executionToken);
                    await AppendEventAsync(
                        snapshot.ExecutionId,
                        ExecutionEventTypes.ValidationCompleted,
                        new
                        {
                            validation.Passed,
                            validation.Quality,
                            checks = validation.Checks.Count,
                            failures = validation.Failures.Count,
                            source = resolver.Name
                        },
                        executionToken);
                    var reusedValidation = new ValidationSummary(
                        true,
                        validation.Quality,
                        decision.Checks.Concat(validation.Checks).Distinct(StringComparer.Ordinal).ToArray(),
                        []);
                    var reusedOutcome = new TaskOutcome(
                        candidate.Output,
                        candidate.Level,
                        validation.Quality,
                        candidate.Evidence,
                        candidate.Usage ?? new UsageSummary(),
                        candidate.Artifact is null ? [] : [candidate.Artifact],
                        reusedValidation,
                        new ContextManifest(
                            0,
                            0,
                            0,
                            0,
                            false,
                            false,
                            [],
                            new Dictionary<string, EvidenceReference>(StringComparer.Ordinal)));
                    return await FinishAsync(snapshot, reusedOutcome, executionToken);
                }
            }

            snapshot = await TransitionAsync(snapshot, ExecutionStatus.Planning, executionToken);
            var routing = await taskRouter.RouteAsync(request, definition, executionToken);
            var plan = routing.Plan;
            EnsureModelBudgetAllows(request, plan);
            planValidator.EnsureValid(plan);
            await AppendRoutingEventsAsync(snapshot.ExecutionId, routing.Decision, executionToken);
            await AppendEventAsync(
                snapshot.ExecutionId,
                ExecutionEventTypes.PlanValidated,
                new
                {
                    plan.PlanId,
                    plan.Version,
                    steps = plan.Steps.Count,
                    plan.Budget.MaxModelCalls,
                    plan.Budget.MaxToolCalls,
                    plan.Budget.MaxTaskDepth,
                    plan.Budget.DeadlineMilliseconds
                },
                executionToken);
            var initialization = await checkpoints.InitializeAsync(
                snapshot.ExecutionId,
                request,
                plan,
                routing.Decision,
                clock.UtcNow,
                executionToken);
            if (initialization.Created)
            {
                await AppendEventAsync(
                    snapshot.ExecutionId,
                    ExecutionEventTypes.WorkflowCheckpointed,
                    new { plan.PlanId, steps = plan.Steps.Count },
                    executionToken);
            }

            var policy = policyEngine.Evaluate(request, definition, plan);
            await AppendPolicyEventAsync(snapshot, policy, executionToken);
            if (policy.Decision == PolicyDecisionKind.Denied)
            {
                throw new TaskExecutionException(
                    policy.Code ?? ErrorCodes.ExecutionFailed,
                    "Task policy denied execution",
                    policy.Reason ?? "The task policy denied execution.");
            }

            EnsurePlanMatchesDefinition(plan, definition);
            if (policy.Decision == PolicyDecisionKind.ApprovalRequired)
            {
                var approval = await CreateApprovalAsync(
                    snapshot,
                    request,
                    plan,
                    policy,
                    executionToken);
                snapshot = await TransitionAsync(snapshot, ExecutionStatus.WaitingForApproval, executionToken);
                await AppendEventAsync(
                    snapshot.ExecutionId,
                    ExecutionEventTypes.ApprovalRequired,
                    new
                    {
                        actionId = approval.ActionId,
                        capability = approval.Capability,
                        sideEffectClass = approval.SideEffectClass,
                        requiredPermissionScopes = approval.RequiredPermissionScopes,
                        requestedByActorId = approval.RequestedByActorId,
                        inputReference = approval.InputReference,
                        idempotencyReference = approval.IdempotencyReference,
                        policyVersion = policy.PolicyVersion
                    },
                    executionToken);
                return snapshot;
            }

            if (deferExecution)
            {
                return await QueueAsync(snapshot, ExecutionStatus.Planning, executionToken);
            }

            return await RunPlanAsync(
                snapshot,
                request,
                definition,
                plan,
                routing.Decision,
                false,
                executionToken);
        }
        catch (OperationCanceledException)
        {
            var timedOut = deadlineSource?.IsCancellationRequested is true &&
                !registeredCancellation.IsCancellationRequested;
            return await TerminalAsync(
                snapshot,
                timedOut ? ExecutionStatus.TimedOut : ExecutionStatus.Cancelled,
                timedOut
                    ? new Problem(ErrorCodes.ExecutionTimedOut, "Execution timed out", "The execution exceeded its deadline.", true)
                    : new Problem(ErrorCodes.ExecutionCancelled, "Execution cancelled", "The execution was cancelled."),
                CancellationToken.None);
        }
        catch (ContextCompilationException exception)
        {
            return await TerminalAsync(
                snapshot,
                ExecutionStatus.Failed,
                new Problem(exception.Code, exception.Title, exception.Message),
                CancellationToken.None);
        }
        catch (RoutingException exception)
        {
            return await TerminalAsync(
                snapshot,
                ExecutionStatus.Failed,
                new Problem(exception.Code, exception.Title, exception.Message),
                CancellationToken.None);
        }
        catch (TaskExecutionException exception)
        {
            return await TerminalAsync(
                snapshot,
                ExecutionStatus.Failed,
                new Problem(exception.Code, exception.Title, exception.Message, exception.Retryable),
                CancellationToken.None);
        }
        catch (InvalidExecutionPlanException exception)
        {
            return await TerminalAsync(
                snapshot,
                ExecutionStatus.Failed,
                new Problem(
                    ErrorCodes.InvalidExecutionPlan,
                    "Execution plan is invalid",
                    exception.Message,
                    Metadata: new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["issueCount"] = exception.Issues.Count.ToString(
                            System.Globalization.CultureInfo.InvariantCulture)
                    }),
                CancellationToken.None);
        }
        catch (WorkflowStepTimedOutException exception)
        {
            return await TerminalAsync(
                snapshot,
                ExecutionStatus.TimedOut,
                new Problem(
                    ErrorCodes.WorkflowStepTimedOut,
                    "Workflow step timed out",
                    exception.Message,
                    true,
                    new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["stepId"] = exception.StepId
                    }),
                CancellationToken.None);
        }
        catch (WorkflowStepExecutionException exception)
        {
            return await TerminalAsync(
                snapshot,
                ExecutionStatus.Failed,
                new Problem(
                    ErrorCodes.WorkflowStepFailed,
                    "Workflow step failed",
                    exception.Message,
                    Metadata: new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["stepId"] = exception.StepId
                    }),
                CancellationToken.None);
        }
        catch (ModelGatewayException exception)
        {
            var metadata = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["gatewayFailureKind"] = exception.FailureKind.ToString()
            };
            if (exception.StatusCode is { } statusCode)
            {
                metadata["gatewayStatusCode"] = statusCode.ToString(
                    System.Globalization.CultureInfo.InvariantCulture);
            }

            if (!string.IsNullOrWhiteSpace(exception.GatewayId))
            {
                metadata["gatewayId"] = exception.GatewayId;
            }

            return await TerminalAsync(
                snapshot,
                ExecutionStatus.Failed,
                new Problem(
                    exception.Code,
                    "Model gateway failed",
                    exception.Message,
                    exception.Retryable,
                    metadata),
                CancellationToken.None);
        }
        catch (CapabilityInvocationException exception)
        {
            return await TerminalAsync(
                snapshot,
                ExecutionStatus.Failed,
                CapabilityProblem(exception),
                CancellationToken.None);
        }
        catch (ExternalActionExecutionException exception)
        {
            return await TerminalAsync(
                snapshot,
                ExecutionStatus.Failed,
                new Problem(
                    exception.Code,
                    exception.Title,
                    exception.Message,
                    exception.Retryable),
                CancellationToken.None);
        }
        catch (Exception exception)
        {
            return await TerminalAsync(
                snapshot,
                ExecutionStatus.Failed,
                new Problem(ErrorCodes.ExecutionFailed, "Execution failed", exception.Message),
                CancellationToken.None);
        }
        finally
        {
            executionSource?.Dispose();
            deadlineSource?.Dispose();
            if (cancellationRegistered)
            {
                cancellations.Complete(snapshot.ExecutionId);
            }
        }
    }

    public Task<ApprovalResult> SubmitApprovalAsync(
        Guid executionId,
        ApprovalDecision decision,
        string tenantId,
        string actorId,
        IReadOnlyCollection<string> permissionScopes,
        CancellationToken cancellationToken) =>
        SubmitApprovalCoreAsync(
            executionId,
            decision,
            tenantId,
            actorId,
            permissionScopes,
            false,
            cancellationToken);

    public Task<ApprovalResult> SubmitApprovalForQueueAsync(
        Guid executionId,
        ApprovalDecision decision,
        string tenantId,
        string actorId,
        IReadOnlyCollection<string> permissionScopes,
        CancellationToken cancellationToken) =>
        SubmitApprovalCoreAsync(
            executionId,
            decision,
            tenantId,
            actorId,
            permissionScopes,
            true,
            cancellationToken);

    private async Task<ApprovalResult> SubmitApprovalCoreAsync(
        Guid executionId,
        ApprovalDecision decision,
        string tenantId,
        string actorId,
        IReadOnlyCollection<string> permissionScopes,
        bool deferExecution,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(decision.ActionId))
        {
            throw new ApprovalSubmissionException(
                ErrorCodes.InvalidTaskRequest,
                "Invalid approval decision",
                "ActionId is required.");
        }

        var snapshot = await store.GetAsync(executionId, cancellationToken);
        if (snapshot is null || !string.Equals(snapshot.TenantId, tenantId, StringComparison.Ordinal))
        {
            throw new ApprovalSubmissionException(
                ErrorCodes.ApprovalNotFound,
                "Approval not found",
                "The requested approval was not found.");
        }

        using var trace = _telemetry.StartExecution(snapshot, permissionScopes, "resume");

        var approval = await approvals.GetAsync(executionId, decision.ActionId, cancellationToken);
        if (approval is null || !string.Equals(approval.TenantId, tenantId, StringComparison.Ordinal))
        {
            throw new ApprovalSubmissionException(
                ErrorCodes.ApprovalNotFound,
                "Approval not found",
                "The requested approval was not found.");
        }

        if (IsTerminal(snapshot.Status) && approval.Status == ApprovalStatus.Pending)
        {
            throw new ApprovalSubmissionException(
                ErrorCodes.ApprovalAlreadyDecided,
                "Execution is no longer awaiting approval",
                $"Execution '{executionId}' is already {snapshot.Status}.");
        }

        var approverPolicy = policyEngine.EvaluateApproval(approval, permissionScopes);
        await AppendPolicyEventAsync(snapshot, approverPolicy, cancellationToken, actorId);
        if (approverPolicy.Decision == PolicyDecisionKind.Denied)
        {
            throw new ApprovalSubmissionException(
                approverPolicy.Code ?? ErrorCodes.PermissionScopeDenied,
                "Approval permission denied",
                approverPolicy.Reason ?? "The actor is not authorized to decide this approval.");
        }

        ApprovalDecisionResult decisionResult;
        try
        {
            decisionResult = await approvals.DecideAsync(
                executionId,
                decision.ActionId,
                decision.Approved,
                actorId,
                decision.Reason,
                clock.UtcNow,
                cancellationToken);
        }
        catch (InvalidOperationException exception)
        {
            throw new ApprovalSubmissionException(
                ErrorCodes.ApprovalAlreadyDecided,
                "Approval already decided",
                exception.Message);
        }

        approval = decisionResult.Approval;
        if (decisionResult.Applied)
        {
            await AppendEventAsync(
                executionId,
                ExecutionEventTypes.ApprovalDecided,
                new
                {
                    actionId = approval.ActionId,
                    status = approval.Status,
                    decidedByActorId = approval.DecidedByActorId,
                    decidedAt = approval.DecidedAt,
                    reasonProvided = approval.Reason is not null,
                    policyVersion = TaskPolicyEngine.CurrentPolicyVersion
                },
                cancellationToken);
        }

        snapshot = await store.GetAsync(executionId, cancellationToken) ?? snapshot;
        if (!decision.Approved)
        {
            if (!IsTerminal(snapshot.Status))
            {
                snapshot = await TerminalAsync(
                    snapshot,
                    ExecutionStatus.Failed,
                    new Problem(
                        ErrorCodes.ApprovalDenied,
                        "External action denied",
                        "The proposed external action was denied by an authorized actor."),
                    CancellationToken.None);
            }

            return new ApprovalResult(approval.ToSnapshot(), snapshot);
        }

        if (IsTerminal(snapshot.Status))
        {
            return new ApprovalResult(approval.ToSnapshot(), snapshot);
        }

        if (snapshot.Status != ExecutionStatus.WaitingForApproval)
        {
            throw new ApprovalSubmissionException(
                ErrorCodes.ApprovalAlreadyDecided,
                "Execution is not awaiting approval",
                $"Execution '{executionId}' is currently {snapshot.Status}.");
        }

        var checkpoint = await checkpoints.GetAsync(executionId, cancellationToken)
            ?? throw new ApprovalSubmissionException(
                ErrorCodes.ExecutionFailed,
                "Workflow checkpoint missing",
                "The approved execution has no durable workflow checkpoint.");
        var definition = await taskDefinitions.FindAsync(checkpoint.Request.TaskType, cancellationToken)
            ?? throw new ApprovalSubmissionException(
                ErrorCodes.UnknownTaskType,
                "Unknown task type",
                $"No active task definition exists for '{checkpoint.Request.TaskType}'.");
        var executionPolicy = policyEngine.Evaluate(
            checkpoint.Request,
            definition,
            checkpoint.Plan,
            approval);
        await AppendPolicyEventAsync(snapshot, executionPolicy, cancellationToken, actorId);
        if (executionPolicy.Decision != PolicyDecisionKind.Allowed)
        {
            snapshot = await TerminalAsync(
                snapshot,
                ExecutionStatus.Failed,
                new Problem(
                    executionPolicy.Code ?? ErrorCodes.ExecutionFailed,
                    "Approved action failed policy revalidation",
                    executionPolicy.Reason ?? "The approved action no longer passes policy."),
                CancellationToken.None);
            return new ApprovalResult(approval.ToSnapshot(), snapshot);
        }

        if (deferExecution)
        {
            snapshot = await QueueAsync(
                snapshot,
                ExecutionStatus.WaitingForApproval,
                cancellationToken);
            return new ApprovalResult(approval.ToSnapshot(), snapshot);
        }

        var resumedAt = clock.UtcNow;
        var claimed = await store.TryTransitionAsync(
            executionId,
            ExecutionStatus.WaitingForApproval,
            ExecutionStatus.Running,
            resumedAt,
            cancellationToken);
        if (claimed is null)
        {
            var current = await store.GetAsync(executionId, cancellationToken)
                ?? throw new ApprovalSubmissionException(
                    ErrorCodes.ApprovalNotFound,
                    "Approval not found",
                    "The approved execution no longer exists.");
            if (current.Status != ExecutionStatus.WaitingForApproval)
            {
                return new ApprovalResult(approval.ToSnapshot(), current);
            }

            throw new ApprovalSubmissionException(
                ErrorCodes.ApprovalAlreadyDecided,
                "Execution is not awaiting approval",
                $"Execution '{executionId}' could not be claimed for approved execution.");
        }

        snapshot = claimed;
        var registeredCancellation = default(CancellationToken);
        CancellationTokenSource? deadlineSource = null;
        CancellationTokenSource? executionSource = null;
        var cancellationRegistered = false;
        try
        {
            await AppendEventAsync(
                executionId,
                ExecutionEventTypes.StatusChanged,
                new { from = ExecutionStatus.WaitingForApproval, to = ExecutionStatus.Running },
                cancellationToken);
            registeredCancellation = cancellations.Register(executionId, cancellationToken);
            cancellationRegistered = true;
            deadlineSource = new CancellationTokenSource();
            deadlineSource.CancelAfter(
                TimeSpan.FromMilliseconds(checkpoint.Plan.Budget.DeadlineMilliseconds));
            executionSource = CancellationTokenSource.CreateLinkedTokenSource(
                registeredCancellation,
                deadlineSource.Token);
            snapshot = await RunPlanAsync(
                snapshot,
                checkpoint.Request,
                definition,
                checkpoint.Plan,
                checkpoint.Routing,
                false,
                executionSource.Token);
        }
        catch (Exception exception)
        {
            snapshot = await HandleResumedFailureAsync(
                snapshot,
                exception,
                deadlineSource?.IsCancellationRequested is true &&
                    !registeredCancellation.IsCancellationRequested);
        }
        finally
        {
            executionSource?.Dispose();
            deadlineSource?.Dispose();
            if (cancellationRegistered)
            {
                cancellations.Complete(executionId);
            }
        }

        return new ApprovalResult(approval.ToSnapshot(), snapshot);
    }

    public async Task<ExecutionSnapshot> ProcessQueuedAsync(
        Guid executionId,
        CancellationToken cancellationToken)
    {
        var snapshot = await store.GetAsync(executionId, cancellationToken)
            ?? throw new KeyNotFoundException($"Execution '{executionId}' was not found.");
        if (IsTerminal(snapshot.Status))
        {
            return snapshot;
        }

        if (snapshot.Status is not (ExecutionStatus.Queued or ExecutionStatus.Running))
        {
            throw new InvalidOperationException(
                $"Execution '{executionId}' cannot be processed from {snapshot.Status}.");
        }

        var checkpoint = await checkpoints.GetAsync(executionId, cancellationToken)
            ?? throw new InvalidOperationException(
                $"Queued execution '{executionId}' has no durable workflow checkpoint.");
        var definition = await taskDefinitions.FindAsync(checkpoint.Request.TaskType, cancellationToken)
            ?? throw new InvalidOperationException(
                $"No active task definition exists for '{checkpoint.Request.TaskType}'.");
        using var trace = _telemetry.StartExecution(
            snapshot,
            checkpoint.Request.PermissionScopes ?? [],
            "worker");
        var remainingDeadline = await RemainingWorkerDeadlineAsync(
            executionId,
            checkpoint.Plan.Budget.DeadlineMilliseconds,
            cancellationToken);
        if (remainingDeadline <= TimeSpan.Zero)
        {
            await CancelCheckpointAsync(executionId, CancellationToken.None);
            return await TerminalAsync(
                snapshot,
                ExecutionStatus.TimedOut,
                new Problem(
                    ErrorCodes.ExecutionTimedOut,
                    "Execution timed out",
                    "The execution exceeded its durable queue deadline.",
                    true),
                CancellationToken.None);
        }

        using var deadline = new CancellationTokenSource(remainingDeadline);
        using var execution = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            deadline.Token);
        try
        {
            if (snapshot.CancellationRequestedAt is not null)
            {
                await CancelCheckpointAsync(executionId, CancellationToken.None);
                return await TerminalAsync(
                    snapshot,
                    ExecutionStatus.Cancelled,
                    new Problem(
                        ErrorCodes.ExecutionCancelled,
                        "Execution cancelled",
                        "The execution was cancelled before a worker started it."),
                    CancellationToken.None);
            }

            return await RunPlanAsync(
                snapshot,
                checkpoint.Request,
                definition,
                checkpoint.Plan,
                checkpoint.Routing,
                true,
                execution.Token);
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested)
        {
            await CancelCheckpointAsync(executionId, CancellationToken.None);
            return await TerminalAsync(
                snapshot,
                ExecutionStatus.TimedOut,
                new Problem(
                    ErrorCodes.ExecutionTimedOut,
                    "Execution timed out",
                    "The execution exceeded its worker deadline.",
                    true),
                CancellationToken.None);
        }
        catch (OperationCanceledException)
        {
            var latest = await store.GetAsync(executionId, CancellationToken.None) ?? snapshot;
            if (latest.CancellationRequestedAt is null)
            {
                throw;
            }

            await CancelCheckpointAsync(executionId, CancellationToken.None);
            return await TerminalAsync(
                latest,
                ExecutionStatus.Cancelled,
                new Problem(
                    ErrorCodes.ExecutionCancelled,
                    "Execution cancelled",
                    "The execution was cancelled while running."),
                CancellationToken.None);
        }
        catch (Exception exception) when (IsExecutionFailure(exception))
        {
            return await HandleResumedFailureAsync(snapshot, exception, false);
        }
    }

    private async Task<ExecutionSnapshot> RunPlanAsync(
        ExecutionSnapshot snapshot,
        TaskRequest request,
        TaskDefinition definition,
        ExecutionPlan plan,
        RoutingDecision routing,
        bool preserveCheckpointOnCancellation,
        CancellationToken cancellationToken)
    {
        if (snapshot.Status != ExecutionStatus.Running)
        {
            snapshot = await TransitionAsync(snapshot, ExecutionStatus.Running, cancellationToken);
        }
        var context = await contextCompiler.CompileAsync(request, definition, cancellationToken);
        await AppendEventAsync(
            snapshot.ExecutionId,
            ExecutionEventTypes.ContextCompiled,
            new
            {
                estimatedTokens = context.Manifest.EstimatedTokens,
                budgetTokens = context.Manifest.BudgetTokens,
                projectedInputTokens = context.Manifest.ProjectedInputTokens,
                contextTokens = context.Manifest.ContextTokens,
                truncated = context.Manifest.Truncated,
                fullHistoryIncluded = context.Manifest.FullHistoryIncluded,
                entries = context.Manifest.Entries.Count,
                included = context.Manifest.Entries.Count(entry => entry.Included),
                provenance = context.Manifest.Provenance.Count
            },
            cancellationToken);

        var modelGatewayBudgets = ModelGatewayBudgets(plan);
        var workflow = await scheduler.ExecuteAsync(
            snapshot.ExecutionId,
            request,
            plan,
            routing,
            async (step, dependencyOutputs, stepCancellationToken) =>
            {
                var result = step.Kind switch
                {
                    ExecutionStepKind.Model => await ExecuteModelStepAsync(
                        snapshot.ExecutionId,
                        request,
                        definition,
                        context,
                        step,
                        modelGatewayBudgets[step.Id],
                        dependencyOutputs,
                        stepCancellationToken),
                    ExecutionStepKind.Tool when step.SideEffectClass < SideEffectClass.ReversibleWrite =>
                        await ExecuteCapabilityStepAsync(
                            snapshot,
                            request,
                            definition,
                            step,
                            stepCancellationToken),
                    ExecutionStepKind.Tool when step.SideEffectClass >= SideEffectClass.ReversibleWrite =>
                        await ExecuteExternalActionAsync(
                            snapshot,
                            request,
                            step,
                            stepCancellationToken),
                    _ => throw new WorkflowStepExecutionException(
                        step.Id,
                        $"Capability '{step.Capability}' has no registered executor.")
                };
                return JsonSerializer.SerializeToElement(result, ContractJsonOptions);
            },
            cancellationToken,
            preserveCheckpointOnCancellation);
        if (!workflow.Outputs.TryGetValue("execute", out var resultOutput))
        {
            throw new WorkflowStepExecutionException(
                "execute",
                "The direct workflow completed without an 'execute' step output.");
        }

        var finalResult = resultOutput.Deserialize<ModelGatewayResult>(ContractJsonOptions)
            ?? throw new WorkflowStepExecutionException(
                "execute",
                "The checkpointed capability result is invalid.");
        var capabilityResult = AggregateWorkflowResult(plan, workflow.Outputs, finalResult);
        if (capabilityResult.Resilience is { } resilience)
        {
            routing = routing with
            {
                SelectedDeployment = resilience.FinalDeployment,
                Resilience = resilience
            };
        }
        var usage = capabilityResult.Usage;
        EnsureUsageWithinBudget(request, capabilityResult);
        if (usage.ModelCalls > 0)
        {
            await AppendEventAsync(
                snapshot.ExecutionId,
                ExecutionEventTypes.GatewayCompleted,
                new
                {
                    capability = routing.SelectedCapability,
                    profileId = routing.SelectedProfileId,
                    gatewayId = capabilityResult.GatewayId,
                    provider = capabilityResult.Deployment?.Provider,
                    deploymentId = capabilityResult.Deployment?.DeploymentId,
                    region = capabilityResult.Deployment?.Region,
                    residency = capabilityResult.Deployment?.Residency,
                    modelVersion = capabilityResult.Deployment?.ModelVersion,
                    transport = capabilityResult.Transport,
                    finishReason = capabilityResult.FinishReason,
                    inputTokens = usage.InputTokens,
                    outputTokens = usage.OutputTokens,
                    cost = usage.Cost,
                    durationMilliseconds = usage.DurationMilliseconds,
                    modelCalls = usage.ModelCalls,
                    fallbackAttempts = capabilityResult.Resilience?.Attempts.Count ?? 0,
                    peakQueuedSteps = workflow.PeakQueuedSteps,
                    backpressureWaitCount = workflow.BackpressureWaitCount,
                    recoveredStepCount = workflow.RecoveredStepCount
                },
                cancellationToken);
        }

        snapshot = await TransitionAsync(snapshot, ExecutionStatus.Validating, cancellationToken);
        var validator = validators.First(x => x.Supports(request.TaskType));
        var validation = await validator.ValidateAsync(
            request,
            definition,
            capabilityResult,
            context,
            cancellationToken);
        await AppendEventAsync(
            snapshot.ExecutionId,
            ExecutionEventTypes.ValidationCompleted,
            new
            {
                validation.Passed,
                validation.Quality,
                checks = validation.Checks.Count,
                failures = validation.Failures.Count
            },
            cancellationToken);
        if (!validation.Passed)
        {
            throw new TaskExecutionException(
                ErrorCodes.ValidationFailed,
                "Task validation failed",
                string.Join(" ", validation.Failures));
        }

        snapshot = await TransitionAsync(snapshot, ExecutionStatus.Materializing, cancellationToken);
        var memory = await MaterializeProjectMemoryAsync(snapshot, request, cancellationToken);
        var combinedEvidence = capabilityResult.Evidence
            .Concat(context.Evidence)
            .DistinctBy(x => (x.Kind, x.Reference))
            .ToArray();
        var createdAt = clock.UtcNow;
        var dependencies = combinedEvidence
            .Select(item => new DependencyReference(item.Kind, item.Reference, item.ContentHash))
            .Concat(memory.Select(item => new DependencyReference(
                "memory",
                item.MemoryId.ToString(),
                item.ContentHash)))
            .DistinctBy(item => (item.Kind, item.Reference))
            .ToArray();
        var logicalKey = request.Metadata?.GetValueOrDefault("artifactKey")?.Trim();
        var artifact = await artifacts.SaveAsync(
            new ArtifactRecord(
                Guid.CreateVersion7(),
                snapshot.TenantId,
                request.ProjectId,
                request.TaskType,
                definition.Version,
                definition.ArtifactType,
                1,
                fingerprint.Create(request, definition.Version),
                CanonicalJson.Hash(capabilityResult.Output),
                capabilityResult.Output.Clone(),
                combinedEvidence,
                createdAt,
                definition.ArtifactTimeToLive is { } ttl ? createdAt.Add(ttl) : null,
                true,
                string.IsNullOrWhiteSpace(logicalKey) ? request.TaskType : logicalKey,
                Dependencies: dependencies),
            cancellationToken);
        await AppendEventAsync(
            snapshot.ExecutionId,
            ExecutionEventTypes.ArtifactMaterialized,
            new
            {
                artifact.ArtifactId,
                artifact.ArtifactType,
                artifact.Version,
                artifact.ContentHash,
                artifact.LogicalKey,
                artifact.LifecycleStatus,
                artifact.SupersedesArtifactId,
                dependencies = artifact.EffectiveDependencies.Count
            },
            cancellationToken);
        if (artifact.SupersedesArtifactId is not null)
        {
            await AppendEventAsync(
                snapshot.ExecutionId,
                ExecutionEventTypes.ArtifactSuperseded,
                new
                {
                    artifact.ArtifactId,
                    artifact.SupersedesArtifactId,
                    artifact.LogicalKey,
                    artifact.Version
                },
                cancellationToken);
        }

        var outcome = new TaskOutcome(
            capabilityResult.Output,
            ResolutionLevelFor(routing),
            validation.Quality,
            combinedEvidence,
            usage,
            [artifact.ToReference()],
            validation.ToContract(),
            context.Manifest,
            routing);
        return await FinishMaterializedAsync(snapshot, outcome, cancellationToken);
    }

    private async Task<ModelGatewayResult> ExecuteModelStepAsync(
        Guid executionId,
        TaskRequest request,
        TaskDefinition definition,
        CompiledContext context,
        ExecutionPlanStep step,
        ModelStepGatewayBudget gatewayBudget,
        IReadOnlyDictionary<string, JsonElement> dependencyOutputs,
        CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        var modelContext = AddProjectedCapabilityOutputs(
            request,
            definition,
            context,
            dependencyOutputs);
        var gatewayRequest = new ModelGatewayRequest(
            step.Capability,
            context.ProjectedInput,
            modelContext,
            request.Constraints?.MaxOutputTokens ?? definition.DefaultMaxOutputTokens,
            executionId.ToString(),
            step.ProfileId,
            step.TimeoutMilliseconds,
            MinimumQuality: Math.Max(
                request.Constraints?.MinimumQuality ?? definition.MinimumQuality,
                definition.MinimumQuality),
            MaximumCost: gatewayBudget.MaximumCost,
            AllowedRegions: request.Constraints?.AllowedRegions,
            RequiredResidency: request.Constraints?.RequiredResidency,
            MaximumAttempts: gatewayBudget.MaximumAttempts);
        await AppendEventAsync(
            executionId,
            ExecutionEventTypes.GatewayStarted,
                new
                {
                    stepId = step.Id,
                    capability = step.Capability,
                step.ProfileId,
                gatewayId = modelGateway.GatewayId,
                deadlineMilliseconds = step.TimeoutMilliseconds
            },
            cancellationToken);

        var streamEvents = 0;
        var deltaEvents = 0;
        var deltaCharacters = 0;
        var usageUpdates = 0;
        var previousSequence = 0L;
        ModelGatewayResult? result = null;
        try
        {
            await foreach (var streamEvent in modelGateway.StreamAsync(
                gatewayRequest,
                cancellationToken))
            {
                streamEvents++;
                if (streamEvent.Sequence <= previousSequence)
                {
                    throw InvalidGatewayStream(
                        gatewayRequest,
                        "Model gateway stream event sequences must increase monotonically.");
                }

                previousSequence = streamEvent.Sequence;
                switch (streamEvent.Kind)
                {
                    case ModelGatewayStreamEventKind.OutputDelta when streamEvent.Delta is { } delta:
                        deltaEvents++;
                        deltaCharacters = checked(deltaCharacters + delta.Length);
                        break;
                    case ModelGatewayStreamEventKind.Usage when streamEvent.Usage is not null:
                        usageUpdates++;
                        break;
                    case ModelGatewayStreamEventKind.Completed when
                        streamEvent.Result is not null && result is null:
                        result = streamEvent.Result;
                        break;
                    default:
                        throw InvalidGatewayStream(
                            gatewayRequest,
                            $"The model gateway emitted an invalid {streamEvent.Kind} event.");
                }
            }

            cancellationToken.ThrowIfCancellationRequested();

            if (result is null)
            {
                throw InvalidGatewayStream(
                    gatewayRequest,
                    "The model gateway stream ended without a completed result.");
            }
        }
        catch (ModelGatewayException exception)
        {
            stopwatch.Stop();
            if (exception.Resilience is { } resilience)
            {
                await AppendGatewayResilienceEvidenceAsync(
                    executionId,
                    resilience,
                    CancellationToken.None);
            }
            await AppendGatewayFailureAsync(
                executionId,
                step,
                exception.ToFailure(),
                stopwatch.ElapsedMilliseconds);
            throw;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            stopwatch.Stop();
            await AppendGatewayFailureAsync(
                executionId,
                step,
                new ModelGatewayFailure(
                    ErrorCodes.ExecutionCancelled,
                    ModelGatewayFailureKind.Cancelled,
                    "The model gateway call was cancelled.",
                    false,
                    GatewayId: modelGateway.GatewayId,
                    CorrelationId: gatewayRequest.CorrelationId),
                stopwatch.ElapsedMilliseconds);
            throw;
        }

        stopwatch.Stop();
        var normalized = result with
        {
            Usage = result.Usage with
            {
                DurationMilliseconds = stopwatch.ElapsedMilliseconds,
                ModelCalls = Math.Max(1, result.Usage.ModelCalls)
            }
        };
        if (normalized.Resilience is { } resilienceTrace)
        {
            await AppendGatewayResilienceEvidenceAsync(
                executionId,
                resilienceTrace,
                cancellationToken);
        }
        if (normalized.Transport == ModelGatewayTransport.Streaming)
        {
            await AppendEventAsync(
                executionId,
                ExecutionEventTypes.GatewayStreamed,
                new
                {
                    stepId = step.Id,
                    gatewayId = normalized.GatewayId,
                    streamEvents,
                    deltaEvents,
                    deltaCharacters,
                    usageUpdates
                },
                cancellationToken);
        }

        return normalized;
    }

    private async Task<ModelGatewayResult> ExecuteCapabilityStepAsync(
        ExecutionSnapshot snapshot,
        TaskRequest request,
        TaskDefinition definition,
        ExecutionPlanStep step,
        CancellationToken cancellationToken)
    {
        await AppendEventAsync(
            snapshot.ExecutionId,
            ExecutionEventTypes.CapabilityStarted,
            new
            {
                stepId = step.Id,
                step.Capability,
                version = 1,
                sideEffectClass = step.SideEffectClass,
                deadlineMilliseconds = step.TimeoutMilliseconds
            },
            cancellationToken);
        try
        {
            var result = await capabilityExecutor.ExecuteAsync(
                new CapabilityInvocationRequest(
                    step.Capability,
                    1,
                    request.Input,
                    snapshot.TenantId,
                    snapshot.ActorId,
                    request.ProjectId,
                    request.PermissionScopes ?? [],
                    TaskPolicyEngine.CurrentPolicyVersion,
                    step.SideEffectClass,
                    step.TimeoutMilliseconds,
                    MaximumCapabilityOutputBytes(request, definition),
                    snapshot.ExecutionId.ToString()),
                cancellationToken);
            await AppendEventAsync(
                snapshot.ExecutionId,
                ExecutionEventTypes.CapabilityCompleted,
                new
                {
                    stepId = step.Id,
                    capability = result.Metadata.Capability,
                    version = result.Metadata.Version,
                    connectorId = result.Metadata.ConnectorId,
                    kind = result.Metadata.Kind,
                    trustLevel = result.Metadata.TrustLevel,
                    transport = result.Metadata.Transport,
                    projected = result.Metadata.Projected,
                    outputReference = result.Metadata.OutputReference,
                    durationMilliseconds = result.Usage.DurationMilliseconds,
                    toolCalls = result.Usage.ToolCalls
                },
                CancellationToken.None);
            return new ModelGatewayResult(
                result.Output,
                result.Usage,
                result.Confidence,
                result.Evidence);
        }
        catch (CapabilityInvocationException exception)
        {
            var failure = exception.ToFailure();
            await AppendEventAsync(
                snapshot.ExecutionId,
                ExecutionEventTypes.CapabilityFailed,
                new
                {
                    stepId = step.Id,
                    code = failure.Code,
                    kind = failure.Kind,
                    retryable = failure.Retryable,
                    capability = failure.Capability,
                    connectorId = failure.ConnectorId
                },
                CancellationToken.None);
            throw;
        }
    }

    private static JsonElement AddProjectedCapabilityOutputs(
        TaskRequest request,
        TaskDefinition definition,
        CompiledContext context,
        IReadOnlyDictionary<string, JsonElement> dependencyOutputs)
    {
        if (dependencyOutputs.Count == 0)
        {
            return context.Content;
        }

        var projected = new SortedDictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var dependency in dependencyOutputs)
        {
            var result = dependency.Value.Deserialize<ModelGatewayResult>(ContractJsonOptions)
                ?? throw new WorkflowStepExecutionException(
                    dependency.Key,
                    $"Dependency '{dependency.Key}' has an invalid normalized result envelope.");
            if (result.Usage.ToolCalls > 0)
            {
                projected[dependency.Key] = result.Output.Clone();
            }
        }

        if (projected.Count == 0)
        {
            return context.Content;
        }

        var combined = new SortedDictionary<string, JsonElement>(StringComparer.Ordinal);
        if (context.Content.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in context.Content.EnumerateObject())
            {
                combined[property.Name] = property.Value.Clone();
            }
        }

        combined["capabilityOutputs"] = JsonSerializer.SerializeToElement(projected, ContractJsonOptions);
        var serialized = JsonSerializer.SerializeToElement(combined, ContractJsonOptions);
        var budget = Math.Max(1, request.Constraints?.MaxInputTokens ?? definition.DefaultMaxInputTokens);
        var estimated = TokenEstimator.Estimate(context.ProjectedInput) + TokenEstimator.Estimate(serialized);
        if (estimated > budget)
        {
            throw new ContextCompilationException(
                ErrorCodes.ContextBudgetExceeded,
                "Capability context budget exceeded",
                $"Projected capability output would require {estimated} estimated tokens, above the task limit of {budget}.");
        }

        return serialized;
    }

    private static int MaximumCapabilityOutputBytes(
        TaskRequest request,
        TaskDefinition definition)
    {
        var tokenLimit = Math.Clamp(
            request.Constraints?.MaxOutputTokens ?? definition.DefaultMaxOutputTokens,
            256,
            16 * 1024);
        return tokenLimit * 4;
    }

    private async Task AppendGatewayResilienceEvidenceAsync(
        Guid executionId,
        GatewayResilienceTrace trace,
        CancellationToken cancellationToken)
    {
        foreach (var candidate in trace.Candidates)
        {
            await AppendEventAsync(
                executionId,
                ExecutionEventTypes.GatewayCandidateEvaluated,
                new
                {
                    candidate.Deployment.GatewayId,
                    candidate.Deployment.Provider,
                    candidate.Deployment.DeploymentId,
                    candidate.Deployment.Region,
                    candidate.Deployment.Residency,
                    candidate.Deployment.ModelVersion,
                    candidate.Eligible,
                    candidate.Reason,
                    candidate.CircuitState,
                    candidate.FailureClass
                },
                cancellationToken);
        }

        foreach (var attempt in trace.Attempts)
        {
            var fallbackSelected = IsGatewayFallback(trace, attempt);
            _telemetry.RecordGatewayAttempt(attempt, fallbackSelected);
            await AppendEventAsync(
                executionId,
                ExecutionEventTypes.GatewayAttempted,
                new
                {
                    attempt.Deployment.GatewayId,
                    attempt.Deployment.Provider,
                    attempt.Deployment.DeploymentId,
                    attempt.Deployment.Region,
                    attempt.Deployment.Residency,
                    attempt.Deployment.ModelVersion,
                    attempt.Attempt,
                    attempt.CircuitStateBefore,
                    attempt.CircuitStateAfter,
                    attempt.Succeeded,
                    attempt.FailureClass,
                    attempt.FailureCode,
                    attempt.StatusCode,
                    attempt.DurationMilliseconds,
                    attempt.RetryAfterMilliseconds,
                    fallbackSelected
                },
                cancellationToken);
            if (fallbackSelected)
            {
                await AppendEventAsync(
                    executionId,
                    ExecutionEventTypes.GatewayFallbackSelected,
                    new
                    {
                        attempt.Deployment.GatewayId,
                        attempt.Deployment.Provider,
                        attempt.Deployment.DeploymentId,
                        attempt.Deployment.Region,
                        attempt.Deployment.ModelVersion,
                        attempt.Attempt,
                        reason = trace.FallbackReason
                    },
                    cancellationToken);
            }

            if (attempt.CircuitStateBefore != attempt.CircuitStateAfter)
            {
                await AppendEventAsync(
                    executionId,
                    ExecutionEventTypes.GatewayCircuitChanged,
                    new
                    {
                        attempt.Deployment.GatewayId,
                        attempt.Deployment.Provider,
                        attempt.Deployment.DeploymentId,
                        attempt.Deployment.Region,
                        attempt.Deployment.ModelVersion,
                        from = attempt.CircuitStateBefore,
                        to = attempt.CircuitStateAfter,
                        attempt.FailureClass
                    },
                    cancellationToken);
            }
        }

        if (trace.ExhaustionReason is not null)
        {
            await AppendEventAsync(
                executionId,
                ExecutionEventTypes.GatewayExhausted,
                new
                {
                    trace.PolicyVersion,
                    trace.ExhaustionReason,
                    candidates = trace.Candidates.Count,
                    attempts = trace.Attempts.Count
                },
                cancellationToken);
        }

        await AppendEventAsync(
            executionId,
            ExecutionEventTypes.GatewayResilienceDecided,
            new
            {
                trace.PolicyVersion,
                finalGatewayId = trace.FinalDeployment?.GatewayId,
                finalProvider = trace.FinalDeployment?.Provider,
                finalDeploymentId = trace.FinalDeployment?.DeploymentId,
                finalRegion = trace.FinalDeployment?.Region,
                finalResidency = trace.FinalDeployment?.Residency,
                finalModelVersion = trace.FinalDeployment?.ModelVersion,
                trace.FallbackReason,
                trace.ExhaustionReason,
                candidates = trace.Candidates.Count,
                attempts = trace.Attempts.Count
            },
            cancellationToken);
    }

    private static bool IsGatewayFallback(
        GatewayResilienceTrace trace,
        GatewayAttemptEvidence attempt)
    {
        if (attempt.Attempt > 1)
        {
            return true;
        }

        foreach (var candidate in trace.Candidates)
        {
            if (candidate.Eligible &&
                string.Equals(
                    candidate.Deployment.DeploymentId,
                    attempt.Deployment.DeploymentId,
                    StringComparison.Ordinal))
            {
                return false;
            }

            if (!candidate.Eligible && candidate.FailureClass is not GatewayFailureClass.Policy)
            {
                return true;
            }
        }

        return false;
    }

    private async Task AppendGatewayFailureAsync(
        Guid executionId,
        ExecutionPlanStep step,
        ModelGatewayFailure failure,
        long durationMilliseconds) =>
        await AppendEventAsync(
            executionId,
            ExecutionEventTypes.GatewayFailed,
            new
            {
                stepId = step.Id,
                step.Capability,
                step.ProfileId,
                code = failure.Code,
                kind = failure.Kind,
                retryable = failure.Retryable,
                statusCode = failure.StatusCode,
                gatewayId = failure.GatewayId,
                failureClass = failure.FailureClass,
                retryAfterMilliseconds = failure.RetryAfterMilliseconds,
                durationMilliseconds
            },
            CancellationToken.None);

    private ModelGatewayException InvalidGatewayStream(
        ModelGatewayRequest request,
        string message) =>
        new(
            ErrorCodes.ModelGatewayInvalidResponse,
            message,
            false,
            failureKind: ModelGatewayFailureKind.InvalidResponse,
            gatewayId: modelGateway.GatewayId,
            correlationId: request.CorrelationId);

    private async Task<ModelGatewayResult> ExecuteExternalActionAsync(
        ExecutionSnapshot snapshot,
        TaskRequest request,
        ExecutionPlanStep step,
        CancellationToken cancellationToken)
    {
        var inputReference = CanonicalJson.Hash(request.Input);
        var idempotencyReference = PolicyReferences.CreateActionIdempotencyReference(
            snapshot.TenantId,
            request.IdempotencyKey!,
            step.Id,
            step.Capability);
        var now = clock.UtcNow;
        var reservation = await externalActions.ReserveAsync(
            new ExternalActionRecord(
                snapshot.ExecutionId,
                snapshot.TenantId,
                step.Id,
                step.Capability,
                idempotencyReference,
                inputReference,
                ExternalActionStatus.Running,
                now,
                now),
            cancellationToken);
        if (reservation.Kind == ExternalActionReservationKind.Reused)
        {
            var reused = reservation.Action.Result
                ?? throw new ExternalActionExecutionException(
                    ErrorCodes.ExternalActionFailed,
                    "External action result missing",
                    "The completed external action has no durable result.");
            await AppendEventAsync(
                snapshot.ExecutionId,
                ExecutionEventTypes.ExternalActionReused,
                new
                {
                    actionId = step.Id,
                    capability = step.Capability,
                    inputReference,
                    idempotencyReference,
                    resultReference = CanonicalJson.Hash(reused.Output)
                },
                cancellationToken);
            return new ModelGatewayResult(
                reused.Output,
                new UsageSummary(ToolCalls: 1),
                1m,
                reused.Evidence);
        }

        if (reservation.Kind != ExternalActionReservationKind.Acquired)
        {
            var (code, title, detail, retryable) = reservation.Kind switch
            {
                ExternalActionReservationKind.Conflict => (
                    ErrorCodes.ExternalActionIdempotencyConflict,
                    "External action idempotency conflict",
                    "The idempotency reference is already bound to a different action or input.",
                    false),
                ExternalActionReservationKind.InProgress => (
                    ErrorCodes.ExternalActionInProgress,
                    "External action already in progress",
                    "A previous attempt reserved this action; reconciliation is required before retrying.",
                    true),
                _ => (
                    ErrorCodes.ExternalActionFailed,
                    "External action previously failed",
                    "The idempotent external action is in a failed state and was not executed again.",
                    false)
            };
            throw new ExternalActionExecutionException(code, title, detail, retryable);
        }

        await AppendEventAsync(
            snapshot.ExecutionId,
            ExecutionEventTypes.ExternalActionStarted,
            new
            {
                actionId = step.Id,
                capability = step.Capability,
                sideEffectClass = step.SideEffectClass,
                actorId = snapshot.ActorId,
                inputReference,
                idempotencyReference
            },
            cancellationToken);
        try
        {
            var stopwatch = Stopwatch.StartNew();
            var result = await externalActionExecutor.ExecuteAsync(
                new ExternalActionRequest(
                    snapshot.ExecutionId,
                    step.Id,
                    step.Capability,
                    request.Input,
                    idempotencyReference,
                    snapshot.TenantId,
                    snapshot.ActorId,
                    request.ProjectId,
                    request.PermissionScopes,
                    TaskPolicyEngine.CurrentPolicyVersion,
                    step.SideEffectClass,
                    step.TimeoutMilliseconds),
                cancellationToken);
            stopwatch.Stop();
            // The side effect has returned success. Persist that fact even if cancellation raced
            // with the response; treating a known success as indeterminate would invite a repeat.
            await externalActions.CompleteAsync(
                snapshot.TenantId,
                idempotencyReference,
                result,
                clock.UtcNow,
                CancellationToken.None);
            await AppendEventAsync(
                snapshot.ExecutionId,
                ExecutionEventTypes.ExternalActionCompleted,
                new
                {
                    actionId = step.Id,
                    capability = step.Capability,
                    inputReference,
                    idempotencyReference,
                    resultReference = CanonicalJson.Hash(result.Output),
                    durationMilliseconds = stopwatch.ElapsedMilliseconds
                },
                CancellationToken.None);
            return new ModelGatewayResult(
                result.Output,
                new UsageSummary(DurationMilliseconds: stopwatch.ElapsedMilliseconds, ToolCalls: 1),
                1m,
                result.Evidence);
        }
        catch (OperationCanceledException)
        {
            await AppendEventAsync(
                snapshot.ExecutionId,
                ExecutionEventTypes.ExternalActionFailed,
                new
                {
                    actionId = step.Id,
                    capability = step.Capability,
                    inputReference,
                    idempotencyReference,
                    status = "indeterminate",
                    retryable = false
                },
                CancellationToken.None);
            throw;
        }
        catch (Exception exception)
        {
            var problem = new Problem(
                ErrorCodes.ExternalActionFailed,
                "External action failed",
                exception.Message);
            await externalActions.FailAsync(
                snapshot.TenantId,
                idempotencyReference,
                problem,
                clock.UtcNow,
                CancellationToken.None);
            await AppendEventAsync(
                snapshot.ExecutionId,
                ExecutionEventTypes.ExternalActionFailed,
                new
                {
                    actionId = step.Id,
                    capability = step.Capability,
                    inputReference,
                    idempotencyReference,
                    status = "failed",
                    retryable = false
                },
                CancellationToken.None);
            throw new ExternalActionExecutionException(
                problem.Code,
                problem.Title,
                problem.Detail,
                innerException: exception);
        }
    }

    private async Task<ApprovalRecord> CreateApprovalAsync(
        ExecutionSnapshot snapshot,
        TaskRequest request,
        ExecutionPlan plan,
        PolicyEvaluation policy,
        CancellationToken cancellationToken)
    {
        var step = plan.Steps.LastOrDefault(item =>
            string.Equals(item.Capability, policy.Capability, StringComparison.Ordinal) &&
            item.SideEffectClass == policy.SideEffectClass)
            ?? throw new InvalidExecutionPlanException(
            [
                new ExecutionPlanValidationIssue(
                    "approval_action_missing",
                    "steps",
                    "The policy-selected approval action does not exist in the execution plan.")
            ]);
        var approval = new ApprovalRecord(
            snapshot.ExecutionId,
            snapshot.TenantId,
            step.Id,
            ApprovalStatus.Pending,
            step.Capability,
            step.SideEffectClass,
            policy.RequiredPermissionScopes,
            snapshot.ActorId,
            null,
            CanonicalJson.Hash(request.Input),
            PolicyReferences.CreateActionIdempotencyReference(
                snapshot.TenantId,
                request.IdempotencyKey!,
                step.Id,
                step.Capability),
            clock.UtcNow);
        return await approvals.CreatePendingAsync(approval, cancellationToken);
    }

    private async Task AppendPolicyEventAsync(
        ExecutionSnapshot snapshot,
        PolicyEvaluation policy,
        CancellationToken cancellationToken,
        string? actorId = null)
    {
        var data = new
        {
            policyVersion = policy.PolicyVersion,
            decision = policy.Decision,
            capability = policy.Capability,
            sideEffectClass = policy.SideEffectClass,
            requiredPermissionScopes = policy.RequiredPermissionScopes,
            missingPermissionScopes = policy.MissingPermissionScopes,
            code = policy.Code,
            actorId = actorId ?? snapshot.ActorId,
            tenantId = snapshot.TenantId,
            projectId = snapshot.ProjectId
        };
        await AppendEventAsync(
            snapshot.ExecutionId,
            ExecutionEventTypes.PolicyEvaluated,
            data,
            cancellationToken);
        if (policy.Decision == PolicyDecisionKind.Denied)
        {
            await AppendEventAsync(
                snapshot.ExecutionId,
                ExecutionEventTypes.CapabilityDenied,
                data,
                cancellationToken);
        }
    }

    private async Task<ExecutionSnapshot> HandleResumedFailureAsync(
        ExecutionSnapshot snapshot,
        Exception exception,
        bool timedOut)
    {
        var (status, problem) = exception switch
        {
            OperationCanceledException when timedOut => (
                ExecutionStatus.TimedOut,
                new Problem(ErrorCodes.ExecutionTimedOut, "Execution timed out", "The execution exceeded its deadline.", true)),
            OperationCanceledException => (
                ExecutionStatus.Cancelled,
                new Problem(ErrorCodes.ExecutionCancelled, "Execution cancelled", "The execution was cancelled.")),
            TaskExecutionException task => (
                ExecutionStatus.Failed,
                new Problem(task.Code, task.Title, task.Message, task.Retryable)),
            ContextCompilationException context => (
                ExecutionStatus.Failed,
                new Problem(context.Code, context.Title, context.Message)),
            RoutingException routing => (
                ExecutionStatus.Failed,
                new Problem(routing.Code, routing.Title, routing.Message)),
            ExternalActionExecutionException action => (
                ExecutionStatus.Failed,
                new Problem(action.Code, action.Title, action.Message, action.Retryable)),
            CapabilityInvocationException capability => (
                ExecutionStatus.Failed,
                CapabilityProblem(capability)),
            WorkflowStepTimedOutException step => (
                ExecutionStatus.TimedOut,
                new Problem(
                    ErrorCodes.WorkflowStepTimedOut,
                    "Workflow step timed out",
                    step.Message,
                    true,
                    new Dictionary<string, string> { ["stepId"] = step.StepId })),
            WorkflowStepExecutionException step => (
                ExecutionStatus.Failed,
                new Problem(
                    ErrorCodes.WorkflowStepFailed,
                    "Workflow step failed",
                    step.Message,
                    Metadata: new Dictionary<string, string> { ["stepId"] = step.StepId })),
            ModelGatewayException gateway => (
                ExecutionStatus.Failed,
                new Problem(gateway.Code, "Model gateway failed", gateway.Message, gateway.Retryable)),
            _ => (
                ExecutionStatus.Failed,
                new Problem(ErrorCodes.ExecutionFailed, "Execution failed", exception.Message))
        };
        return await TerminalAsync(snapshot, status, problem, CancellationToken.None);
    }

    private static Problem CapabilityProblem(CapabilityInvocationException exception)
    {
        var metadata = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["capabilityFailureKind"] = exception.FailureKind.ToString()
        };
        if (!string.IsNullOrWhiteSpace(exception.Capability))
        {
            metadata["capability"] = exception.Capability;
        }

        if (!string.IsNullOrWhiteSpace(exception.ConnectorId))
        {
            metadata["connectorId"] = exception.ConnectorId;
        }

        return new Problem(
            exception.Code,
            "Capability invocation failed",
            exception.Message,
            exception.Retryable,
            metadata);
    }

    private async Task<IReadOnlyList<MemoryRecord>> MaterializeProjectMemoryAsync(
        ExecutionSnapshot snapshot,
        TaskRequest request,
        CancellationToken cancellationToken)
    {
        var results = await projectMemory.MaterializeAsync(
            request,
            snapshot.ExecutionId,
            cancellationToken);
        foreach (var result in results)
        {
            if (result.Write.Created)
            {
                await AppendEventAsync(
                    snapshot.ExecutionId,
                    ExecutionEventTypes.MemoryMaterialized,
                    new
                    {
                        result.Write.Record.MemoryId,
                        result.Write.Record.Kind,
                        result.Write.Record.Key,
                        result.Write.Record.Version,
                        result.Write.Record.ContentHash,
                        result.Write.Record.LifecycleStatus
                    },
                    cancellationToken);
            }

            if (result.Write.Previous is not null)
            {
                await AppendEventAsync(
                    snapshot.ExecutionId,
                    ExecutionEventTypes.MemorySuperseded,
                    new
                    {
                        memoryId = result.Write.Record.MemoryId,
                        supersedesMemoryId = result.Write.Previous.MemoryId,
                        result.Write.Record.Kind,
                        result.Write.Record.Key,
                        result.Write.Record.Version
                    },
                    cancellationToken);
            }

            if (result.InvalidatedMemory.MemoryIds.Count > 0)
            {
                await AppendEventAsync(
                    snapshot.ExecutionId,
                    ExecutionEventTypes.MemoryInvalidated,
                    new
                    {
                        sourceMemoryId = result.Write.Previous?.MemoryId,
                        memoryIds = result.InvalidatedMemory.MemoryIds,
                        reason = "dependency_superseded"
                    },
                    cancellationToken);
            }

            if (result.InvalidatedArtifacts.ArtifactIds.Count > 0)
            {
                await AppendEventAsync(
                    snapshot.ExecutionId,
                    ExecutionEventTypes.ArtifactInvalidated,
                    new
                    {
                        sourceMemoryId = result.Write.Previous?.MemoryId,
                        artifactIds = result.InvalidatedArtifacts.ArtifactIds,
                        reason = "dependency_superseded"
                    },
                    cancellationToken);
            }
        }

        return results.Select(result => result.Write.Record).ToArray();
    }

    private async Task<ExecutionSnapshot> FinishAsync(
        ExecutionSnapshot snapshot,
        TaskOutcome outcome,
        CancellationToken cancellationToken)
    {
        snapshot = await TransitionAsync(snapshot, ExecutionStatus.Materializing, cancellationToken);
        return await FinishMaterializedAsync(snapshot, outcome, cancellationToken);
    }

    private async Task<ExecutionSnapshot> FinishMaterializedAsync(
        ExecutionSnapshot snapshot,
        TaskOutcome outcome,
        CancellationToken cancellationToken)
    {
        snapshot = snapshot with { Outcome = outcome, UpdatedAt = clock.UtcNow };
        await store.UpdateAsync(snapshot, cancellationToken);
        snapshot = await TransitionAsync(snapshot, ExecutionStatus.Succeeded, cancellationToken);
        await AppendEventAsync(
            snapshot.ExecutionId,
            ExecutionEventTypes.Completed,
            new
            {
                snapshot.Status,
                outcome.ResolutionLevel,
                outcome.Confidence,
                artifacts = outcome.Artifacts.Count
            },
            cancellationToken);
        _telemetry.RecordTerminal(snapshot);
        return snapshot;
    }

    private async Task<ExecutionSnapshot> QueueAsync(
        ExecutionSnapshot snapshot,
        ExecutionStatus expectedStatus,
        CancellationToken cancellationToken)
    {
        var work = executionWork ?? throw new InvalidOperationException(
            "Durable execution work is not configured for asynchronous submission.");
        var queuedAt = clock.UtcNow;
        await work.EnqueueAsync(
            snapshot.ExecutionId,
            expectedStatus,
            queuedAt,
            cancellationToken);
        var queued = snapshot with { Status = ExecutionStatus.Queued, UpdatedAt = queuedAt };
        await AppendEventAsync(
            queued.ExecutionId,
            ExecutionEventTypes.StatusChanged,
            new { from = expectedStatus, to = ExecutionStatus.Queued },
            cancellationToken);
        await AppendEventAsync(
            queued.ExecutionId,
            ExecutionEventTypes.Queued,
            new { availableAt = queuedAt },
            cancellationToken);
        return queued;
    }

    private Task CancelCheckpointAsync(Guid executionId, CancellationToken cancellationToken) =>
        checkpoints.CancelIncompleteStepsAsync(
            executionId,
            new Problem(
                ErrorCodes.ExecutionCancelled,
                "Execution stopped",
                "The durable execution stopped before all steps completed."),
            clock.UtcNow,
            cancellationToken);

    private async Task<TimeSpan> RemainingWorkerDeadlineAsync(
        Guid executionId,
        int deadlineMilliseconds,
        CancellationToken cancellationToken)
    {
        DateTimeOffset? queuedAt = null;
        await foreach (var item in store.ReadEventsAsync(executionId, 0, cancellationToken))
        {
            if (item.Type == ExecutionEventTypes.Queued)
            {
                queuedAt = item.OccurredAt;
            }
        }

        var startedAt = queuedAt ?? clock.UtcNow;
        return startedAt.AddMilliseconds(deadlineMilliseconds) - clock.UtcNow;
    }

    private async Task<ExecutionSnapshot> TerminalAsync(
        ExecutionSnapshot snapshot,
        ExecutionStatus terminal,
        Problem problem,
        CancellationToken cancellationToken)
    {
        var latest = await store.GetAsync(snapshot.ExecutionId, cancellationToken) ?? snapshot;
        if (IsTerminal(latest.Status))
        {
            return latest;
        }

        ExecutionStateMachine.EnsureCanTransition(latest.Status, terminal);
        var updated = latest with { Status = terminal, UpdatedAt = clock.UtcNow, Error = problem };
        await store.UpdateAsync(updated, cancellationToken);
        await AppendEventAsync(
            updated.ExecutionId,
            ExecutionEventTypes.StatusChanged,
            new { from = latest.Status, to = terminal },
            cancellationToken);
        await AppendEventAsync(
            updated.ExecutionId,
            ExecutionEventTypes.Failed,
            new { status = terminal, problem.Code, problem.Title, problem.Retryable },
            cancellationToken);
        _telemetry.RecordTerminal(updated);
        return updated;
    }

    private async Task<ExecutionSnapshot> TransitionAsync(
        ExecutionSnapshot snapshot,
        ExecutionStatus target,
        CancellationToken cancellationToken)
    {
        ExecutionStateMachine.EnsureCanTransition(snapshot.Status, target);
        var updated = snapshot with { Status = target, UpdatedAt = clock.UtcNow };
        await store.UpdateAsync(updated, cancellationToken);
        await AppendEventAsync(
            updated.ExecutionId,
            ExecutionEventTypes.StatusChanged,
            new { from = snapshot.Status, to = target },
            cancellationToken);
        return updated;
    }

    private async Task<ExecutionEvent> AppendEventAsync(
        Guid executionId,
        string eventType,
        object data,
        CancellationToken cancellationToken)
    {
        var executionEvent = await store.AppendEventAsync(
            executionId,
            eventType,
            clock.UtcNow,
            JsonSerializer.SerializeToElement(data),
            cancellationToken);
        _telemetry.RecordEvent(eventType);
        return executionEvent;
    }

    private Task<ExecutionEvent> AppendResolutionDecisionAsync(
        Guid executionId,
        string resolver,
        ResolutionDecision decision,
        CancellationToken cancellationToken) =>
        AppendEventAsync(
            executionId,
            ExecutionEventTypes.ResolutionConsidered,
            new
            {
                resolver,
                accepted = decision.Accepted,
                code = decision.Code,
                reason = decision.Reason,
                permissionChecked = decision.PermissionChecked,
                freshnessChecked = decision.FreshnessChecked,
                checks = decision.Checks.Count,
                level = decision.Candidate?.Level.ToString()
            },
            cancellationToken);

    private async Task AppendRoutingEventsAsync(
        Guid executionId,
        RoutingDecision decision,
        CancellationToken cancellationToken)
    {
        var data = new
        {
            policyVersion = decision.PolicyVersion,
            path = decision.Path,
            decision.Reason,
            selectedCapability = decision.SelectedCapability,
            selectedProfileId = decision.SelectedProfileId,
            selectedModelTier = decision.SelectedModelTier,
            qualityFloor = decision.QualityFloor,
            expectedQuality = decision.ExpectedQuality,
            expectedCost = decision.ExpectedCost,
            expectedLatencyMilliseconds = decision.ExpectedLatencyMilliseconds,
            uncertainty = decision.Uncertainty,
            score = decision.Score,
            plannerInvoked = decision.PlannerInvoked,
            planningCalls = decision.PlanningCalls,
            escalated = decision.Escalated,
            escalationReason = decision.EscalationReason,
            candidates = decision.Candidates
        };
        await AppendEventAsync(
            executionId,
            ExecutionEventTypes.RoutingDecided,
            data,
            cancellationToken);
        if (decision.Escalated)
        {
            await AppendEventAsync(
                executionId,
                ExecutionEventTypes.RoutingEscalated,
                data,
                cancellationToken);
        }
    }

    private static CompiledContext EmptyCompiledContext()
    {
        var content = JsonSerializer.SerializeToElement(new Dictionary<string, object?>());
        return new CompiledContext(
            content,
            new ContextManifest(
                0,
                0,
                0,
                0,
                false,
                false,
                [],
                new Dictionary<string, EvidenceReference>(StringComparer.Ordinal)),
            [],
            content);
    }

    private static void EnsureModelBudgetAllows(TaskRequest request, ExecutionPlan plan)
    {
        if (request.Constraints?.MaxModelCalls is 0 &&
            plan.Steps.Any(step => step.Kind == ExecutionStepKind.Model))
        {
            throw new TaskExecutionException(
                ErrorCodes.ModelBudgetExhausted,
                "Model call budget exhausted",
                "The task cannot be resolved without generation and its model-call budget is zero.");
        }
    }

    private static Dictionary<string, ModelStepGatewayBudget> ModelGatewayBudgets(
        ExecutionPlan plan)
    {
        var modelSteps = plan.Steps
            .Where(step => step.Kind == ExecutionStepKind.Model)
            .ToArray();
        if (modelSteps.Length == 0)
        {
            return new Dictionary<string, ModelStepGatewayBudget>(StringComparer.Ordinal);
        }

        var minimumAttempts = plan.Budget.MaxModelCalls / modelSteps.Length;
        var extraAttempts = plan.Budget.MaxModelCalls % modelSteps.Length;
        var costShare = plan.Budget.MaxCost is { } maximumCost
            ? maximumCost / modelSteps.Length
            : (decimal?)null;
        return modelSteps
            .Select((step, index) => new
            {
                step.Id,
                Budget = new ModelStepGatewayBudget(
                    minimumAttempts + (index < extraAttempts ? 1 : 0),
                    costShare)
            })
            .ToDictionary(item => item.Id, item => item.Budget, StringComparer.Ordinal);
    }

    private static ModelGatewayResult AggregateWorkflowResult(
        ExecutionPlan plan,
        IReadOnlyDictionary<string, JsonElement> outputs,
        ModelGatewayResult finalResult)
    {
        var completed = plan.Steps
            .Where(step => outputs.ContainsKey(step.Id))
            .Select(step => new
            {
                Step = step,
                Result = outputs[step.Id].Deserialize<ModelGatewayResult>(ContractJsonOptions)
                    ?? throw new WorkflowStepExecutionException(
                        step.Id,
                        $"The checkpointed result for step '{step.Id}' is invalid.")
            })
            .ToArray();
        var usage = new UsageSummary(
            completed.Sum(item => item.Result.Usage.InputTokens),
            completed.Sum(item => item.Result.Usage.OutputTokens),
            completed.Sum(item => item.Result.Usage.Cost),
            completed.Sum(item => item.Result.Usage.DurationMilliseconds),
            completed.Sum(item => item.Result.Usage.ModelCalls),
            completed.Sum(item => item.Result.Usage.ToolCalls));
        var evidence = completed
            .SelectMany(item => item.Result.Evidence)
            .DistinctBy(item => (item.Kind, item.Reference, item.ContentHash))
            .ToArray();
        var lastModelResult = completed
            .LastOrDefault(item => item.Step.Kind == ExecutionStepKind.Model)
            ?.Result;
        var modelMetadata = finalResult.Usage.ModelCalls > 0 ? finalResult : lastModelResult;

        return finalResult with
        {
            Usage = usage,
            Evidence = evidence,
            GatewayId = finalResult.GatewayId ?? modelMetadata?.GatewayId,
            Transport = modelMetadata?.Transport ?? finalResult.Transport,
            FinishReason = modelMetadata?.FinishReason ?? finalResult.FinishReason,
            Deployment = finalResult.Deployment ?? modelMetadata?.Deployment,
            Resilience = finalResult.Resilience ?? modelMetadata?.Resilience
        };
    }

    private static void EnsureUsageWithinBudget(TaskRequest request, ModelGatewayResult result)
    {
        if (request.Constraints?.MaxCost is { } maxCost && result.Usage.Cost > maxCost)
        {
            throw new TaskExecutionException(
                ErrorCodes.CostBudgetExceeded,
                "Cost budget exceeded",
                $"The model gateway reported a cost of {result.Usage.Cost}, above the task limit of {maxCost}.");
        }

        if (request.Constraints?.MaxModelCalls is { } maxCalls && result.Usage.ModelCalls > maxCalls)
        {
            throw new TaskExecutionException(
                ErrorCodes.ModelCallBudgetExceeded,
                "Model call budget exceeded",
                $"The task used {result.Usage.ModelCalls} model calls, above the task limit of {maxCalls}.");
        }

        if (request.Constraints?.MaxToolCalls is { } maxToolCalls && result.Usage.ToolCalls > maxToolCalls)
        {
            throw new TaskExecutionException(
                ErrorCodes.ExecutionFailed,
                "Tool call budget exceeded",
                $"The task used {result.Usage.ToolCalls} tool calls, above the task limit of {maxToolCalls}.");
        }
    }

    private static void EnsurePlanMatchesDefinition(ExecutionPlan plan, TaskDefinition definition)
    {
        var issues = new List<ExecutionPlanValidationIssue>();
        if (!string.Equals(plan.TaskType, definition.TaskType, StringComparison.Ordinal) ||
            plan.TaskVersion != definition.Version)
        {
            issues.Add(new(
                "task_definition_mismatch",
                "taskType",
                "The plan task identity does not match the resolved task definition."));
        }

        var required = definition.EffectiveRequiredCapabilities;
        if (plan.Steps.Count != required.Count)
        {
            issues.Add(new(
                "unsupported_plan_shape",
                "steps",
                "The plan step count must match the trusted task definition's required capabilities."));
        }
        else
        {
            for (var index = 0; index < plan.Steps.Count; index++)
            {
                var step = plan.Steps[index];
                if (!string.Equals(step.Capability, required[index], StringComparison.Ordinal) ||
                    !definition.EffectiveAllowedCapabilities.Contains(step.Capability, StringComparer.Ordinal))
                {
                    issues.Add(new(
                        "capability_mismatch",
                        $"steps[{index}].capability",
                        "The plan step does not match the trusted required-capability sequence."));
                }

                if (step.SideEffectClass > definition.SideEffectClass)
                {
                    issues.Add(new(
                        "side_effect_mismatch",
                        $"steps[{index}].sideEffectClass",
                        "A plan step cannot exceed the trusted task side-effect class."));
                }
            }
        }

        if (issues.Count > 0)
        {
            throw new InvalidExecutionPlanException(issues);
        }
    }

    private static ResolutionLevel ResolutionLevelFor(RoutingDecision routing) =>
        routing.SelectedModelTier switch
        {
            ModelTier.Small => ResolutionLevel.SmallModel,
            ModelTier.Verifier => ResolutionLevel.VerifiedOrHuman,
            null => ResolutionLevel.DeterministicCapability,
            _ => ResolutionLevel.StrongModel
        };

    private static bool IsTerminal(ExecutionStatus status) => status is
        ExecutionStatus.Succeeded or
        ExecutionStatus.Failed or
        ExecutionStatus.Cancelled or
        ExecutionStatus.TimedOut;

    private static bool IsExecutionFailure(Exception exception) => exception is
        TaskExecutionException or
        ContextCompilationException or
        RoutingException or
        InvalidExecutionPlanException or
        ExternalActionExecutionException or
        CapabilityInvocationException or
        WorkflowStepTimedOutException or
        WorkflowStepExecutionException or
        ModelGatewayException;

    /// <summary>
    /// Answers a replayed idempotency key: the same payload returns the original execution, a
    /// different payload is a client bug and is reported rather than silently answered with an
    /// unrelated execution.
    /// </summary>
    private static ExecutionSnapshot ReplayOrConflict(
        ExecutionSubmission existing,
        string idempotencyKey,
        string? submissionFingerprint)
    {
        // Executions recorded before submission fingerprints existed have none stored; treat those
        // as retries so an upgrade cannot start rejecting keys that used to work.
        if (existing.InputFingerprint is null ||
            submissionFingerprint is null ||
            string.Equals(existing.InputFingerprint, submissionFingerprint, StringComparison.Ordinal))
        {
            return existing.Execution;
        }

        throw new IdempotencyKeyReusedException(idempotencyKey);
    }

    private static void Validate(TaskRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.TaskType))
        {
            throw new ArgumentException("TaskType is required.", nameof(request));
        }

        if (request.Input.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
        {
            throw new ArgumentException("Input is required.", nameof(request));
        }

        if (request.Constraints?.DeadlineMilliseconds is <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(request), "DeadlineMilliseconds must be positive.");
        }

        if (request.Constraints?.MinimumQuality is < 0 or > 1)
        {
            throw new ArgumentOutOfRangeException(nameof(request), "MinimumQuality must be between zero and one.");
        }

        if (request.Constraints?.AllowedRegions is { } regions &&
            (regions.Count > 64 ||
             regions.Any(region => string.IsNullOrWhiteSpace(region) || region.Length > 200) ||
             regions.Distinct(StringComparer.OrdinalIgnoreCase).Count() != regions.Count))
        {
            throw new ArgumentOutOfRangeException(
                nameof(request),
                "AllowedRegions must contain at most 64 unique non-empty region names of 200 characters or fewer.");
        }

        if (request.Constraints?.RequiredResidency is { } residency &&
            (string.IsNullOrWhiteSpace(residency) || residency.Length > 200))
        {
            throw new ArgumentOutOfRangeException(
                nameof(request),
                "RequiredResidency must be non-empty and no longer than 200 characters.");
        }

        if (request.Metadata?.TryGetValue("artifactKey", out var artifactKey) is true &&
            artifactKey.Trim().Length > 200)
        {
            throw new ArgumentOutOfRangeException(nameof(request), "Metadata artifactKey cannot exceed 200 characters.");
        }
    }

    private sealed class TaskExecutionException(
        string code,
        string title,
        string detail,
        bool retryable = false) : Exception(detail)
    {
        public string Code { get; } = code;
        public string Title { get; } = title;
        public bool Retryable { get; } = retryable;
    }

    private sealed record ModelStepGatewayBudget(
        int MaximumAttempts,
        decimal? MaximumCost);
}
