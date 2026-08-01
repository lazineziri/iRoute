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
    IExecutionTelemetry? executionTelemetry = null)
{
    private static readonly JsonSerializerOptions ContractJsonOptions = new(JsonSerializerDefaults.Web);
    private readonly IExecutionTelemetry _telemetry = executionTelemetry ?? NoOpExecutionTelemetry.Instance;

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
        using var trace = _telemetry.StartExecution(
            snapshot,
            request.PermissionScopes ?? [],
            "execute");
        await store.CreateAsync(snapshot, request.IdempotencyKey, cancellationToken);
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

            return await RunPlanAsync(
                snapshot,
                request,
                definition,
                plan,
                routing.Decision,
                executionToken);
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
                checkpoint.Routing,
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
        RoutingDecision routing,
        CancellationToken cancellationToken)
    {
        snapshot = await TransitionAsync(snapshot, ExecutionStatus.Running, cancellationToken);
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
                    capability = routing.SelectedCapability,
                    profileId = routing.SelectedProfileId,
                    gatewayId = capabilityResult.GatewayId,
                    transport = capabilityResult.Transport,
                    finishReason = capabilityResult.FinishReason,
                    inputTokens = usage.InputTokens,
                    outputTokens = usage.OutputTokens,
                    cost = usage.Cost,
                    durationMilliseconds = usage.DurationMilliseconds,
                    modelCalls = usage.ModelCalls,
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
            step.TimeoutMilliseconds);
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
        var dependencyResults = dependencyOutputs.Values
            .Select(value => value.Deserialize<ModelGatewayResult>(ContractJsonOptions)
                ?? throw new WorkflowStepExecutionException(
                    step.Id,
                    $"A dependency of step '{step.Id}' has an invalid normalized result envelope."))
            .ToArray();
        if (dependencyResults.Length > 0)
        {
            normalized = normalized with
            {
                Usage = normalized.Usage with
                {
                    Cost = normalized.Usage.Cost + dependencyResults.Sum(item => item.Usage.Cost),
                    DurationMilliseconds = normalized.Usage.DurationMilliseconds +
                        dependencyResults.Sum(item => item.Usage.DurationMilliseconds),
                    ModelCalls = normalized.Usage.ModelCalls +
                        dependencyResults.Sum(item => item.Usage.ModelCalls),
                    ToolCalls = normalized.Usage.ToolCalls +
                        dependencyResults.Sum(item => item.Usage.ToolCalls)
                },
                Evidence = normalized.Evidence
                    .Concat(dependencyResults.SelectMany(item => item.Evidence))
                    .DistinctBy(item => (item.Kind, item.Reference, item.ContentHash))
                    .ToArray()
            };
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
}
