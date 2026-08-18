using iRoute.Common;

namespace iRoute.Services;

public sealed partial class ExecutionService
{
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

        var now = clock.GetUtcNow();
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
            snapshot = snapshot with { TaskDefinitionVersion = definition.Version, UpdatedAt = clock.GetUtcNow() };
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
                clock.GetUtcNow(),
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

}
