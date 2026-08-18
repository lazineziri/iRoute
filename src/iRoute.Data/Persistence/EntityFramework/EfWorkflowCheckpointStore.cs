using System.Data;
using System.Text.Json;
using iRoute.Common;
using Microsoft.EntityFrameworkCore;

namespace iRoute.Data;

public sealed class EfWorkflowCheckpointStore(
    IDbContextFactory<IRouteDbContext> contextFactory,
    IExecutionFence? executionFence = null)
    : IWorkflowCheckpointStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly IExecutionFence _executionFence = executionFence ?? new NullExecutionFence();

    public async Task<WorkflowCheckpointInitialization> InitializeAsync(
        Guid executionId,
        TaskRequest request,
        ExecutionPlan plan,
        RoutingDecision routing,
        DateTimeOffset createdAt,
        CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await context.Database.BeginTransactionAsync(
            IsolationLevel.Serializable,
            cancellationToken);
        await EnsureLeaseOwnsAsync(context, executionId, cancellationToken);
        var existing = await context.WorkflowPlans.SingleOrDefaultAsync(
            item => item.ExecutionId == executionId,
            cancellationToken);
        var planJson = JsonSerializer.Serialize(plan, JsonOptions);
        var routingJson = JsonSerializer.Serialize(routing, JsonOptions);
        var created = existing is null;
        if (existing is null)
        {
            context.WorkflowPlans.Add(new WorkflowPlanEntity
            {
                ExecutionId = executionId,
                RequestJson = JsonSerializer.Serialize(request, JsonOptions),
                PlanJson = planJson,
                RoutingJson = routingJson,
                CreatedAtUnixMilliseconds = createdAt.ToUnixTimeMilliseconds(),
                UpdatedAtUnixMilliseconds = createdAt.ToUnixTimeMilliseconds()
            });
            context.WorkflowSteps.AddRange(plan.Steps.Select(step => new WorkflowStepEntity
            {
                ExecutionId = executionId,
                StepId = step.Id,
                Status = WorkflowStepStatus.Pending
            }));
            await context.SaveChangesAsync(cancellationToken);
        }
        else if (!string.Equals(existing.PlanJson, planJson, StringComparison.Ordinal) ||
                 !string.Equals(existing.RoutingJson, routingJson, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("A different execution plan or routing decision is already checkpointed.");
        }

        var checkpoint = await LoadAsync(context, executionId, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new(checkpoint, created);
    }

    public async Task<WorkflowCheckpoint?> GetAsync(
        Guid executionId,
        CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var exists = await context.WorkflowPlans
            .AsNoTracking()
            .AnyAsync(item => item.ExecutionId == executionId, cancellationToken);
        return exists ? await LoadAsync(context, executionId, cancellationToken) : null;
    }

    public async Task<int> RecoverInterruptedStepsAsync(
        Guid executionId,
        DateTimeOffset recoveredAt,
        CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
        await EnsureLeaseOwnsAsync(context, executionId, cancellationToken);
        var steps = await context.WorkflowSteps
            .Where(step => step.ExecutionId == executionId && step.Status == WorkflowStepStatus.Running)
            .ToListAsync(cancellationToken);
        foreach (var step in steps)
        {
            step.Status = WorkflowStepStatus.Pending;
            step.StartedAtUnixMilliseconds = null;
            step.CompletedAtUnixMilliseconds = null;
            step.ErrorJson = null;
        }

        if (steps.Count > 0)
        {
            await TouchPlanAsync(context, executionId, recoveredAt, cancellationToken);
            await context.SaveChangesAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        return steps.Count;
    }

    public async Task<WorkflowStepCheckpoint> StartStepAsync(
        Guid executionId,
        string stepId,
        DateTimeOffset startedAt,
        CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
        await EnsureLeaseOwnsAsync(context, executionId, cancellationToken);
        var step = await GetStepAsync(context, executionId, stepId, cancellationToken);
        if (step.Status != WorkflowStepStatus.Pending)
        {
            throw new InvalidOperationException($"Step '{stepId}' cannot start from {step.Status}.");
        }

        step.Status = WorkflowStepStatus.Running;
        step.Attempt = checked(step.Attempt + 1);
        step.StartedAtUnixMilliseconds = startedAt.ToUnixTimeMilliseconds();
        step.CompletedAtUnixMilliseconds = null;
        step.ErrorJson = null;
        await TouchPlanAsync(context, executionId, startedAt, cancellationToken);
        await context.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return ToCheckpoint(step);
    }

    public async Task CompleteStepAsync(
        Guid executionId,
        string stepId,
        JsonElement output,
        DateTimeOffset completedAt,
        CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
        await EnsureLeaseOwnsAsync(context, executionId, cancellationToken);
        var step = await GetRunningStepAsync(context, executionId, stepId, cancellationToken);
        step.Status = WorkflowStepStatus.Succeeded;
        step.OutputJson = output.GetRawText();
        step.CompletedAtUnixMilliseconds = completedAt.ToUnixTimeMilliseconds();
        step.ErrorJson = null;
        await TouchPlanAsync(context, executionId, completedAt, cancellationToken);
        await context.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task ResetStepForRetryAsync(
        Guid executionId,
        string stepId,
        DateTimeOffset updatedAt,
        CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
        await EnsureLeaseOwnsAsync(context, executionId, cancellationToken);
        var step = await GetRunningStepAsync(context, executionId, stepId, cancellationToken);
        step.Status = WorkflowStepStatus.Pending;
        step.StartedAtUnixMilliseconds = null;
        step.CompletedAtUnixMilliseconds = null;
        step.ErrorJson = null;
        await TouchPlanAsync(context, executionId, updatedAt, cancellationToken);
        await context.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task FailStepAsync(
        Guid executionId,
        string stepId,
        WorkflowStepStatus status,
        Problem problem,
        DateTimeOffset completedAt,
        CancellationToken cancellationToken)
    {
        if (status is not (WorkflowStepStatus.Failed or WorkflowStepStatus.Cancelled or WorkflowStepStatus.TimedOut))
        {
            throw new ArgumentOutOfRangeException(nameof(status), status, "A failed step requires a terminal failure status.");
        }

        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
        await EnsureLeaseOwnsAsync(context, executionId, cancellationToken);
        var step = await GetStepAsync(context, executionId, stepId, cancellationToken);
        if (step.Status == WorkflowStepStatus.Succeeded)
        {
            await transaction.CommitAsync(cancellationToken);
            return;
        }

        if (step.Status != WorkflowStepStatus.Running && status != WorkflowStepStatus.Cancelled)
        {
            throw new InvalidOperationException($"Step '{stepId}' cannot fail from {step.Status}.");
        }

        step.Status = status;
        step.CompletedAtUnixMilliseconds = completedAt.ToUnixTimeMilliseconds();
        step.ErrorJson = JsonSerializer.Serialize(problem, JsonOptions);
        await TouchPlanAsync(context, executionId, completedAt, cancellationToken);
        await context.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task CancelIncompleteStepsAsync(
        Guid executionId,
        Problem problem,
        DateTimeOffset completedAt,
        CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
        await EnsureLeaseOwnsAsync(context, executionId, cancellationToken);
        var steps = await context.WorkflowSteps
            .Where(step =>
                step.ExecutionId == executionId &&
                (step.Status == WorkflowStepStatus.Pending || step.Status == WorkflowStepStatus.Running))
            .ToListAsync(cancellationToken);
        var errorJson = JsonSerializer.Serialize(problem, JsonOptions);
        foreach (var step in steps)
        {
            step.Status = WorkflowStepStatus.Cancelled;
            step.CompletedAtUnixMilliseconds = completedAt.ToUnixTimeMilliseconds();
            step.ErrorJson = errorJson;
        }

        if (steps.Count > 0)
        {
            await TouchPlanAsync(context, executionId, completedAt, cancellationToken);
            await context.SaveChangesAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
    }

    private static async Task<WorkflowCheckpoint> LoadAsync(
        IRouteDbContext context,
        Guid executionId,
        CancellationToken cancellationToken)
    {
        var plan = await context.WorkflowPlans
            .AsNoTracking()
            .SingleAsync(item => item.ExecutionId == executionId, cancellationToken);
        var steps = await context.WorkflowSteps
            .AsNoTracking()
            .Where(step => step.ExecutionId == executionId)
            .OrderBy(step => step.StepId)
            .ToListAsync(cancellationToken);
        return new WorkflowCheckpoint(
            executionId,
            JsonSerializer.Deserialize<TaskRequest>(plan.RequestJson, JsonOptions)
                ?? throw new InvalidOperationException("The workflow request checkpoint is invalid."),
            JsonSerializer.Deserialize<ExecutionPlan>(plan.PlanJson, JsonOptions)
                ?? throw new InvalidOperationException("The workflow plan checkpoint is invalid."),
            JsonSerializer.Deserialize<RoutingDecision>(plan.RoutingJson, JsonOptions)
                ?? throw new InvalidOperationException("The workflow routing checkpoint is invalid."),
            DateTimeOffset.FromUnixTimeMilliseconds(plan.CreatedAtUnixMilliseconds),
            DateTimeOffset.FromUnixTimeMilliseconds(plan.UpdatedAtUnixMilliseconds),
            steps.Select(ToCheckpoint).ToArray());
    }

    private static WorkflowStepCheckpoint ToCheckpoint(WorkflowStepEntity step) => new(
        step.ExecutionId,
        step.StepId,
        step.Status,
        step.Attempt,
        step.StartedAtUnixMilliseconds is { } startedAt
            ? DateTimeOffset.FromUnixTimeMilliseconds(startedAt)
            : null,
        step.CompletedAtUnixMilliseconds is { } completedAt
            ? DateTimeOffset.FromUnixTimeMilliseconds(completedAt)
            : null,
        step.OutputJson is null ? null : JsonSerializer.Deserialize<JsonElement>(step.OutputJson, JsonOptions),
        step.ErrorJson is null ? null : JsonSerializer.Deserialize<Problem>(step.ErrorJson, JsonOptions));

    private static Task<WorkflowStepEntity> GetStepAsync(
        IRouteDbContext context,
        Guid executionId,
        string stepId,
        CancellationToken cancellationToken) =>
        context.WorkflowSteps.SingleAsync(
            step => step.ExecutionId == executionId && step.StepId == stepId,
            cancellationToken);

    private static async Task<WorkflowStepEntity> GetRunningStepAsync(
        IRouteDbContext context,
        Guid executionId,
        string stepId,
        CancellationToken cancellationToken)
    {
        var step = await GetStepAsync(context, executionId, stepId, cancellationToken);
        if (step.Status != WorkflowStepStatus.Running)
        {
            throw new InvalidOperationException($"Step '{stepId}' is not running.");
        }

        return step;
    }

    private static async Task TouchPlanAsync(
        IRouteDbContext context,
        Guid executionId,
        DateTimeOffset updatedAt,
        CancellationToken cancellationToken)
    {
        var plan = await context.WorkflowPlans.SingleAsync(
            item => item.ExecutionId == executionId,
            cancellationToken);
        plan.UpdatedAtUnixMilliseconds = updatedAt.ToUnixTimeMilliseconds();
    }

    private Task EnsureLeaseOwnsAsync(
        IRouteDbContext context,
        Guid executionId,
        CancellationToken cancellationToken) =>
        ExecutionLeaseGuard.EnsureOwnsAsync(
            context,
            _executionFence,
            executionId,
            cancellationToken);
}
