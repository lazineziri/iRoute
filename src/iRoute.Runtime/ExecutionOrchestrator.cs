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
    BoundedDependencyScheduler scheduler,
    IModelGateway modelGateway,
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

            snapshot = await TransitionAsync(snapshot, ExecutionStatus.Planning, executionToken);
            EnsurePolicyAllows(request, definition);
            var plan = planFactory.Create(request, definition);
            planValidator.EnsureValid(plan);
            EnsureDirectPlanMatchesDefinition(plan, definition);
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

            snapshot = await TransitionAsync(snapshot, ExecutionStatus.Running, executionToken);
            var context = await contextCompiler.CompileAsync(request, definition, executionToken);
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
                executionToken);

            var workflow = await scheduler.ExecuteAsync(
                snapshot.ExecutionId,
                request,
                plan,
                async (step, _, stepCancellationToken) =>
                {
                    var stopwatch = Stopwatch.StartNew();
                    var result = await modelGateway.ExecuteAsync(
                        new ModelGatewayRequest(
                            step.Capability,
                            request.Input,
                            context.Content,
                            request.Constraints?.MaxOutputTokens ?? definition.DefaultMaxOutputTokens,
                            snapshot.ExecutionId.ToString()),
                        stepCancellationToken);
                    stopwatch.Stop();
                    var measuredUsage = result.Usage with
                    {
                        DurationMilliseconds = stopwatch.ElapsedMilliseconds,
                        ModelCalls = Math.Max(1, result.Usage.ModelCalls)
                    };
                    return JsonSerializer.SerializeToElement(
                        result with { Usage = measuredUsage },
                        ContractJsonOptions);
                },
                executionToken);
            if (!workflow.Outputs.TryGetValue("execute", out var gatewayOutput))
            {
                throw new WorkflowStepExecutionException(
                    "execute",
                    "The direct workflow completed without an 'execute' step output.");
            }

            var gatewayResult = gatewayOutput.Deserialize<ModelGatewayResult>(ContractJsonOptions)
                ?? throw new WorkflowStepExecutionException(
                    "execute",
                    "The checkpointed model-gateway result is invalid.");
            var usage = gatewayResult.Usage;
            EnsureUsageWithinBudget(request, gatewayResult);
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
                executionToken);

            snapshot = await TransitionAsync(snapshot, ExecutionStatus.Validating, executionToken);
            var validator = validators.First(x => x.Supports(request.TaskType));
            var validation = await validator.ValidateAsync(
                request,
                definition,
                gatewayResult,
                context,
                executionToken);
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
                executionToken);
            if (!validation.Passed)
            {
                throw new TaskExecutionException(
                    ErrorCodes.ValidationFailed,
                    "Task validation failed",
                    string.Join(" ", validation.Failures));
            }

            snapshot = await TransitionAsync(snapshot, ExecutionStatus.Materializing, executionToken);
            var combinedEvidence = gatewayResult.Evidence
                .Concat(context.Evidence)
                .DistinctBy(x => (x.Kind, x.Reference))
                .ToArray();
            var createdAt = clock.UtcNow;
            var artifact = await artifacts.SaveAsync(
                new ArtifactRecord(
                    Guid.CreateVersion7(),
                    tenantId,
                    request.ProjectId,
                    request.TaskType,
                    definition.Version,
                    definition.ArtifactType,
                    1,
                    fingerprint.Create(request, definition.Version),
                    CanonicalJson.Hash(gatewayResult.Output),
                    gatewayResult.Output.Clone(),
                    combinedEvidence,
                    createdAt,
                    definition.ArtifactTimeToLive is { } ttl ? createdAt.Add(ttl) : null,
                    true),
                executionToken);
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
                executionToken);

            var outcome = new TaskOutcome(
                gatewayResult.Output,
                ResolutionLevel.StrongModel,
                validation.Quality,
                combinedEvidence,
                usage,
                [artifact.ToReference()],
                validation.ToContract(),
                context.Manifest);
            return await FinishMaterializedAsync(snapshot, outcome, executionToken);
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

    private static void EnsurePolicyAllows(TaskRequest request, TaskDefinition definition)
    {
        if (definition.SideEffectClass >= SideEffectClass.ReversibleWrite &&
            request.Constraints?.AllowExternalWrites is not true)
        {
            throw new TaskExecutionException(
                ErrorCodes.ExternalWriteNotAllowed,
                "External write not allowed",
                "This task requires explicit external-write permission.");
        }

        if (request.Constraints?.MaxModelCalls is 0)
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
            if (step.Kind != ExecutionStepKind.Model ||
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
