using System.Diagnostics;
using System.Text.Json;
using iRoute.Contracts;
using iRoute.Core;

namespace iRoute.Runtime;

public sealed class ExecutionOrchestrator(
    IExecutionStore store,
    IArtifactStore artifacts,
    IEnumerable<INoModelResolver> resolvers,
    ITaskDefinitionRegistry taskDefinitions,
    IExecutionPlanFactory planFactory,
    IExecutionPlanValidator planValidator,
    ITaskPolicyEngine policyEngine,
    IWorkflowCheckpointStore checkpoints,
    IApprovalStore approvals,
    IExternalActionStore externalActions,
    BoundedDependencyScheduler scheduler,
    IModelGateway modelGateway,
    IExternalActionExecutor externalActionExecutor,
    IContextCompiler contextCompiler,
    IEnumerable<ITaskOutcomeValidator> validators,
    IInputFingerprint fingerprint,
    IExecutionCancellationRegistry cancellations,
    IClock clock)
{
    private static readonly JsonSerializerOptions ContractJsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<ExecutionSnapshot> ExecuteAsync(TaskRequest request, CancellationToken cancellationToken)
    {
        Validate(request);
        var tenantId = RequestScope.Tenant(request);
        var actorId = RequestScope.Actor(request);

        if (!string.IsNullOrWhiteSpace(request.IdempotencyKey))
        {
            var existing = await store.FindByIdempotencyKeyAsync(
                tenantId,
                request.IdempotencyKey,
                cancellationToken);
            if (existing is not null)
            {
                return existing;
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
        await store.CreateAsync(snapshot, request.IdempotencyKey, cancellationToken);
        await AppendEventAsync(
            snapshot.ExecutionId,
            ExecutionEventTypes.Created,
            new { snapshot.TaskType, snapshot.TenantId, snapshot.ActorId, snapshot.ProjectId },
            cancellationToken);

        var registeredCancellation = cancellations.Register(snapshot.ExecutionId, cancellationToken);
        using var deadlineSource = new CancellationTokenSource();
        var requestedDeadline = request.Constraints?.DeadlineMilliseconds ?? 30000;
        deadlineSource.CancelAfter(TimeSpan.FromMilliseconds(requestedDeadline));
        using var executionSource = CancellationTokenSource.CreateLinkedTokenSource(
            registeredCancellation,
            deadlineSource.Token);
        var executionToken = executionSource.Token;

        try
        {
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
                await AppendEventAsync(
                    snapshot.ExecutionId,
                    ExecutionEventTypes.ResolutionConsidered,
                    new
                    {
                        resolver = "ArtifactReuseResolver",
                        accepted = false,
                        reason = "External-write outcomes cannot bypass current permission and approval policy."
                    },
                    executionToken);
            }
            else
            {
                foreach (var resolver in resolvers.OrderBy(x => x.Order))
                {
                    var candidate = await resolver.TryResolveAsync(request, executionToken);
                    await AppendEventAsync(
                        snapshot.ExecutionId,
                        ExecutionEventTypes.ResolutionConsidered,
                        new
                        {
                            resolver = resolver.GetType().Name,
                            accepted = candidate is { IsFresh: true },
                            level = candidate?.Level.ToString()
                        },
                        executionToken);
                    if (candidate is not { IsFresh: true })
                    {
                        continue;
                    }

                    snapshot = await TransitionAsync(snapshot, ExecutionStatus.Validating, executionToken);
                    var reusedValidation = new ValidationSummary(
                        true,
                        candidate.Confidence,
                        ["The reusable artifact passed scope, version, freshness and input-fingerprint checks."],
                        []);
                    var reusedOutcome = new TaskOutcome(
                        candidate.Output,
                        candidate.Level,
                        candidate.Confidence,
                        candidate.Evidence,
                        new UsageSummary(),
                        candidate.Artifact is null ? [] : [candidate.Artifact],
                        reusedValidation,
                        new ContextManifest(0, 0, false, []));
                    return await FinishAsync(snapshot, reusedOutcome, executionToken);
                }
            }

            snapshot = await TransitionAsync(snapshot, ExecutionStatus.Planning, executionToken);
            var plan = planFactory.Create(request, definition);
            EnsureModelBudgetAllows(request, plan);
            planValidator.EnsureValid(plan);
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

            EnsureDirectPlanMatchesDefinition(plan, definition);
            if (policy.Decision == PolicyDecisionKind.ApprovalRequired)
            {
                var approval = await CreateApprovalAsync(
                    snapshot,
                    request,
                    plan.Steps.Single(),
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

            return await RunPlanAsync(snapshot, request, definition, plan, executionToken);
        }
        catch (OperationCanceledException)
        {
            var timedOut = deadlineSource.IsCancellationRequested && !registeredCancellation.IsCancellationRequested;
            return await TerminalAsync(
                snapshot,
                timedOut ? ExecutionStatus.TimedOut : ExecutionStatus.Cancelled,
                timedOut
                    ? new Problem(ErrorCodes.ExecutionTimedOut, "Execution timed out", "The execution exceeded its deadline.", true)
                    : new Problem(ErrorCodes.ExecutionCancelled, "Execution cancelled", "The execution was cancelled."),
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
            var metadata = exception.StatusCode is { } statusCode
                ? new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["gatewayStatusCode"] = statusCode.ToString(System.Globalization.CultureInfo.InvariantCulture)
                }
                : null;
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
            cancellations.Complete(snapshot.ExecutionId);
        }
    }

    public async Task<ApprovalResult> SubmitApprovalAsync(
        Guid executionId,
        ApprovalDecision decision,
        string tenantId,
        string actorId,
        IReadOnlyCollection<string> permissionScopes,
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

        var registeredCancellation = cancellations.Register(executionId, cancellationToken);
        using var deadlineSource = new CancellationTokenSource();
        deadlineSource.CancelAfter(TimeSpan.FromMilliseconds(checkpoint.Plan.Budget.DeadlineMilliseconds));
        using var executionSource = CancellationTokenSource.CreateLinkedTokenSource(
            registeredCancellation,
            deadlineSource.Token);
        try
        {
            snapshot = await RunPlanAsync(
                snapshot,
                checkpoint.Request,
                definition,
                checkpoint.Plan,
                executionSource.Token);
        }
        catch (Exception exception)
        {
            snapshot = await HandleResumedFailureAsync(
                snapshot,
                exception,
                deadlineSource.IsCancellationRequested && !registeredCancellation.IsCancellationRequested);
        }
        finally
        {
            cancellations.Complete(executionId);
        }

        return new ApprovalResult(approval.ToSnapshot(), snapshot);
    }

    private async Task<ExecutionSnapshot> RunPlanAsync(
        ExecutionSnapshot snapshot,
        TaskRequest request,
        TaskDefinition definition,
        ExecutionPlan plan,
        CancellationToken cancellationToken)
    {
        snapshot = await TransitionAsync(snapshot, ExecutionStatus.Running, cancellationToken);
        var context = await contextCompiler.CompileAsync(request, definition, cancellationToken);
        await AppendEventAsync(
            snapshot.ExecutionId,
            ExecutionEventTypes.ContextCompiled,
            new
            {
                context.Manifest.EstimatedTokens,
                context.Manifest.BudgetTokens,
                context.Manifest.Truncated,
                entries = context.Manifest.Entries.Count
            },
            cancellationToken);

        var workflow = await scheduler.ExecuteAsync(
            snapshot.ExecutionId,
            request,
            plan,
            async (step, _, stepCancellationToken) =>
            {
                var result = step.Kind switch
                {
                    ExecutionStepKind.Model => await ExecuteModelStepAsync(
                        snapshot.ExecutionId,
                        request,
                        definition,
                        context,
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
            cancellationToken);
        if (!workflow.Outputs.TryGetValue("execute", out var resultOutput))
        {
            throw new WorkflowStepExecutionException(
                "execute",
                "The direct workflow completed without an 'execute' step output.");
        }

        var capabilityResult = resultOutput.Deserialize<ModelGatewayResult>(ContractJsonOptions)
            ?? throw new WorkflowStepExecutionException(
                "execute",
                "The checkpointed capability result is invalid.");
        var usage = capabilityResult.Usage;
        EnsureUsageWithinBudget(request, capabilityResult);
        if (usage.ModelCalls > 0)
        {
            await AppendEventAsync(
                snapshot.ExecutionId,
                ExecutionEventTypes.GatewayCompleted,
                new
                {
                    definition.Capability,
                    usage.InputTokens,
                    usage.OutputTokens,
                    usage.Cost,
                    usage.DurationMilliseconds,
                    usage.ModelCalls,
                    workflow.PeakQueuedSteps,
                    workflow.BackpressureWaitCount,
                    workflow.RecoveredStepCount
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
        var combinedEvidence = capabilityResult.Evidence
            .Concat(context.Evidence)
            .DistinctBy(x => (x.Kind, x.Reference))
            .ToArray();
        var createdAt = clock.UtcNow;
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
                true),
            cancellationToken);
        await AppendEventAsync(
            snapshot.ExecutionId,
            ExecutionEventTypes.ArtifactMaterialized,
            new
            {
                artifact.ArtifactId,
                artifact.ArtifactType,
                artifact.Version,
                artifact.ContentHash
            },
            cancellationToken);

        var outcome = new TaskOutcome(
            capabilityResult.Output,
            usage.ToolCalls > 0 ? ResolutionLevel.DeterministicCapability : ResolutionLevel.StrongModel,
            validation.Quality,
            combinedEvidence,
            usage,
            [artifact.ToReference()],
            validation.ToContract(),
            context.Manifest);
        return await FinishMaterializedAsync(snapshot, outcome, cancellationToken);
    }

    private async Task<ModelGatewayResult> ExecuteModelStepAsync(
        Guid executionId,
        TaskRequest request,
        TaskDefinition definition,
        CompiledContext context,
        ExecutionPlanStep step,
        CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        var result = await modelGateway.ExecuteAsync(
            new ModelGatewayRequest(
                step.Capability,
                request.Input,
                context.Content,
                request.Constraints?.MaxOutputTokens ?? definition.DefaultMaxOutputTokens,
                executionId.ToString()),
            cancellationToken);
        stopwatch.Stop();
        return result with
        {
            Usage = result.Usage with
            {
                DurationMilliseconds = stopwatch.ElapsedMilliseconds,
                ModelCalls = Math.Max(1, result.Usage.ModelCalls)
            }
        };
    }

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
                    idempotencyReference),
                cancellationToken);
            stopwatch.Stop();
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
        ExecutionPlanStep step,
        PolicyEvaluation policy,
        CancellationToken cancellationToken)
    {
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
            ExternalActionExecutionException action => (
                ExecutionStatus.Failed,
                new Problem(action.Code, action.Title, action.Message, action.Retryable)),
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
        return snapshot;
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

    private Task<ExecutionEvent> AppendEventAsync(
        Guid executionId,
        string eventType,
        object data,
        CancellationToken cancellationToken) =>
        store.AppendEventAsync(
            executionId,
            eventType,
            clock.UtcNow,
            JsonSerializer.SerializeToElement(data),
            cancellationToken);

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

    private static void EnsureDirectPlanMatchesDefinition(ExecutionPlan plan, TaskDefinition definition)
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

        if (plan.Steps.Count != 1)
        {
            issues.Add(new(
                "unsupported_plan_shape",
                "steps",
                "The current direct executor accepts exactly one model step."));
        }
        else
        {
            var step = plan.Steps[0];
            var expectedKind = definition.SideEffectClass == SideEffectClass.None
                ? ExecutionStepKind.Model
                : ExecutionStepKind.Tool;
            if (step.Kind != expectedKind ||
                !string.Equals(step.Capability, definition.Capability, StringComparison.Ordinal))
            {
                issues.Add(new(
                    "capability_mismatch",
                    "steps[0].capability",
                    "The direct plan step does not match the resolved model capability."));
            }
        }

        if (issues.Count > 0)
        {
            throw new InvalidExecutionPlanException(issues);
        }
    }

    private static bool IsTerminal(ExecutionStatus status) => status is
        ExecutionStatus.Succeeded or
        ExecutionStatus.Failed or
        ExecutionStatus.Cancelled or
        ExecutionStatus.TimedOut;

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
}
