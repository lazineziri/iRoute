using System.Data;
using System.Runtime.CompilerServices;
using System.Text.Json;
using iRoute.Contracts;
using iRoute.Core;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace iRoute.Infrastructure;

public sealed record StorageOptions
{
    public string Provider { get; init; } = "Sqlite";
    public bool AutoInitialize { get; init; } = true;
}

public sealed class IRouteDbContext(DbContextOptions<IRouteDbContext> options) : DbContext(options)
{
    public DbSet<ExecutionEntity> Executions => Set<ExecutionEntity>();
    public DbSet<ExecutionEventEntity> ExecutionEvents => Set<ExecutionEventEntity>();
    public DbSet<ExecutionWorkItemEntity> ExecutionWorkItems => Set<ExecutionWorkItemEntity>();
    public DbSet<WorkflowPlanEntity> WorkflowPlans => Set<WorkflowPlanEntity>();
    public DbSet<WorkflowStepEntity> WorkflowSteps => Set<WorkflowStepEntity>();
    public DbSet<ApprovalEntity> Approvals => Set<ApprovalEntity>();
    public DbSet<ExternalActionEntity> ExternalActions => Set<ExternalActionEntity>();
    public DbSet<ArtifactEntity> Artifacts => Set<ArtifactEntity>();
    public DbSet<MemoryEntity> MemoryRecords => Set<MemoryEntity>();
    public DbSet<DependencyEdgeEntity> DependencyEdges => Set<DependencyEdgeEntity>();
    public DbSet<LifecycleArchiveEntity> LifecycleArchives => Set<LifecycleArchiveEntity>();
    public DbSet<GatewayCircuitEntity> GatewayCircuits => Set<GatewayCircuitEntity>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        var execution = modelBuilder.Entity<ExecutionEntity>();
        execution.ToTable("Executions");
        execution.HasKey(x => x.ExecutionId);
        execution.Property(x => x.TaskType).HasMaxLength(120);
        execution.Property(x => x.TenantId).HasMaxLength(200);
        execution.Property(x => x.ActorId).HasMaxLength(200);
        execution.Property(x => x.ProjectId).HasMaxLength(200);
        execution.Property(x => x.IdempotencyKey).HasMaxLength(200);
        execution.Property(x => x.InputFingerprint).HasMaxLength(64);
        execution.Property(x => x.Status).HasConversion<string>().HasMaxLength(40);
        execution.HasIndex(x => new { x.TenantId, x.IdempotencyKey }).IsUnique();

        var executionEvent = modelBuilder.Entity<ExecutionEventEntity>();
        executionEvent.ToTable("ExecutionEvents");
        executionEvent.HasKey(x => new { x.ExecutionId, x.Sequence });
        executionEvent.Property(x => x.EventType).HasMaxLength(120);
        executionEvent.HasIndex(x => new { x.ExecutionId, x.OccurredAtUnixMilliseconds });

        var executionWork = modelBuilder.Entity<ExecutionWorkItemEntity>();
        executionWork.ToTable("ExecutionWorkItems");
        executionWork.HasKey(x => x.ExecutionId);
        executionWork.Property(x => x.State).HasConversion<string>().HasMaxLength(40);
        executionWork.Property(x => x.LeaseOwner).HasMaxLength(200);
        executionWork.HasIndex(x => new { x.State, x.AvailableAtUnixMilliseconds });
        executionWork.HasIndex(x => x.LeaseExpiresAtUnixMilliseconds);
        executionWork.HasOne<ExecutionEntity>()
            .WithOne()
            .HasForeignKey<ExecutionWorkItemEntity>(x => x.ExecutionId)
            .OnDelete(DeleteBehavior.Cascade);

        var workflowPlan = modelBuilder.Entity<WorkflowPlanEntity>();
        workflowPlan.ToTable("WorkflowPlans");
        workflowPlan.HasKey(x => x.ExecutionId);

        var workflowStep = modelBuilder.Entity<WorkflowStepEntity>();
        workflowStep.ToTable("WorkflowSteps");
        workflowStep.HasKey(x => new { x.ExecutionId, x.StepId });
        workflowStep.Property(x => x.StepId).HasMaxLength(64);
        workflowStep.Property(x => x.Status).HasConversion<string>().HasMaxLength(40);
        workflowStep.HasIndex(x => new { x.ExecutionId, x.Status });

        var approval = modelBuilder.Entity<ApprovalEntity>();
        approval.ToTable("Approvals");
        approval.HasKey(x => new { x.ExecutionId, x.ActionId });
        approval.Property(x => x.ActionId).HasMaxLength(64);
        approval.Property(x => x.TenantId).HasMaxLength(200);
        approval.Property(x => x.Capability).HasMaxLength(200);
        approval.Property(x => x.SideEffectClass).HasConversion<string>().HasMaxLength(40);
        approval.Property(x => x.Status).HasConversion<string>().HasMaxLength(40);
        approval.Property(x => x.RequestedByActorId).HasMaxLength(200);
        approval.Property(x => x.DecidedByActorId).HasMaxLength(200);
        approval.Property(x => x.InputReference).HasMaxLength(64);
        approval.Property(x => x.IdempotencyReference).HasMaxLength(64);
        approval.HasIndex(x => new { x.TenantId, x.Status });
        approval.HasOne<ExecutionEntity>()
            .WithMany()
            .HasForeignKey(x => x.ExecutionId)
            .OnDelete(DeleteBehavior.Cascade);

        var externalAction = modelBuilder.Entity<ExternalActionEntity>();
        externalAction.ToTable("ExternalActions");
        externalAction.HasKey(x => new { x.TenantId, x.IdempotencyReference });
        externalAction.Property(x => x.TenantId).HasMaxLength(200);
        externalAction.Property(x => x.IdempotencyReference).HasMaxLength(64);
        externalAction.Property(x => x.ActionId).HasMaxLength(64);
        externalAction.Property(x => x.Capability).HasMaxLength(200);
        externalAction.Property(x => x.InputReference).HasMaxLength(64);
        externalAction.Property(x => x.Status).HasConversion<string>().HasMaxLength(40);
        externalAction.HasIndex(x => new { x.ExecutionId, x.ActionId });
        externalAction.HasIndex(x => x.Status);
        externalAction.HasOne<ExecutionEntity>()
            .WithMany()
            .HasForeignKey(x => x.ExecutionId)
            .OnDelete(DeleteBehavior.Cascade);

        var artifact = modelBuilder.Entity<ArtifactEntity>();
        artifact.ToTable("Artifacts");
        artifact.HasKey(x => x.ArtifactId);
        artifact.Property(x => x.TenantId).HasMaxLength(200);
        artifact.Property(x => x.ProjectId).HasMaxLength(200);
        artifact.Property(x => x.TaskType).HasMaxLength(120);
        artifact.Property(x => x.ArtifactType).HasMaxLength(120);
        artifact.Property(x => x.InputHash).HasMaxLength(64);
        artifact.Property(x => x.ContentHash).HasMaxLength(64);
        artifact.Property(x => x.LogicalKey).HasMaxLength(200);
        artifact.Property(x => x.LifecycleStatus).HasConversion<string>().HasMaxLength(40);
        artifact.HasIndex(x => new
        {
            x.TenantId,
            x.ProjectId,
            x.TaskType,
            x.TaskDefinitionVersion,
            x.InputHash,
            x.IsActive
        });
        artifact.HasIndex(x => new
        {
            x.TenantId,
            x.ProjectId,
            x.ArtifactType,
            x.LogicalKey,
            x.Version
        }).IsUnique();
        artifact.HasIndex(x => new
        {
            x.TenantId,
            x.ProjectId,
            x.ArtifactType,
            x.LogicalKey,
            x.IsActive
        });

        var memory = modelBuilder.Entity<MemoryEntity>();
        memory.ToTable("MemoryRecords");
        memory.HasKey(x => x.MemoryId);
        memory.Property(x => x.TenantId).HasMaxLength(200);
        memory.Property(x => x.ProjectId).HasMaxLength(200);
        memory.Property(x => x.Kind).HasConversion<string>().HasMaxLength(40);
        memory.Property(x => x.Key).HasMaxLength(200);
        memory.Property(x => x.ContentHash).HasMaxLength(64);
        memory.Property(x => x.LifecycleStatus).HasConversion<string>().HasMaxLength(40);
        memory.HasIndex(x => new
        {
            x.TenantId,
            x.ProjectId,
            x.Kind,
            x.Key,
            x.Version
        }).IsUnique();
        memory.HasIndex(x => new
        {
            x.TenantId,
            x.ProjectId,
            x.Kind,
            x.Key,
            x.LifecycleStatus
        });

        var dependency = modelBuilder.Entity<DependencyEdgeEntity>();
        dependency.ToTable("DependencyEdges");
        dependency.HasKey(x => new
        {
            x.SourceKind,
            x.SourceId,
            x.TargetKind,
            x.TargetReference
        });
        dependency.Property(x => x.TenantId).HasMaxLength(200);
        dependency.Property(x => x.SourceKind).HasMaxLength(40);
        dependency.Property(x => x.TargetKind).HasMaxLength(120);
        dependency.Property(x => x.TargetReference).HasMaxLength(500);
        dependency.Property(x => x.TargetContentHash).HasMaxLength(64);
        dependency.HasIndex(x => new
        {
            x.TenantId,
            x.TargetKind,
            x.TargetReference
        });

        var lifecycleArchive = modelBuilder.Entity<LifecycleArchiveEntity>();
        lifecycleArchive.ToTable("LifecycleArchives");
        lifecycleArchive.HasKey(x => new { x.TenantId, x.ResourceKind, x.ResourceId });
        lifecycleArchive.Property(x => x.TenantId).HasMaxLength(200);
        lifecycleArchive.Property(x => x.ResourceKind).HasConversion<string>().HasMaxLength(40);
        lifecycleArchive.Property(x => x.ContentHash).HasMaxLength(64);
        lifecycleArchive.HasIndex(x => new { x.TenantId, x.ArchivedAtUnixMilliseconds });

        var gatewayCircuit = modelBuilder.Entity<GatewayCircuitEntity>();
        gatewayCircuit.ToTable("GatewayCircuits");
        gatewayCircuit.HasKey(x => x.DeploymentId);
        gatewayCircuit.Property(x => x.DeploymentId).HasMaxLength(200);
        gatewayCircuit.Property(x => x.State).HasConversion<string>().HasMaxLength(40);
        gatewayCircuit.Property(x => x.ProbeOwner).HasMaxLength(200);
        gatewayCircuit.Property(x => x.LastFailureClass).HasConversion<string>().HasMaxLength(40);
        gatewayCircuit.HasIndex(x => new { x.State, x.NextProbeAtUnixMilliseconds });
        gatewayCircuit.HasIndex(x => x.ProbeLeaseExpiresAtUnixMilliseconds);
    }
}

public sealed class ExecutionEntity
{
    public Guid ExecutionId { get; set; }
    public string TenantId { get; set; } = null!;
    public string ActorId { get; set; } = null!;
    public string? ProjectId { get; set; }
    public string TaskType { get; set; } = null!;
    public int? TaskDefinitionVersion { get; set; }
    public ExecutionStatus Status { get; set; }
    public long CreatedAtUnixMilliseconds { get; set; }
    public long UpdatedAtUnixMilliseconds { get; set; }
    public long? CancellationRequestedAtUnixMilliseconds { get; set; }
    public string? IdempotencyKey { get; set; }
    public string? InputFingerprint { get; set; }
    public string? OutcomeJson { get; set; }
    public string? ErrorJson { get; set; }
}

public sealed class ExecutionEventEntity
{
    public Guid ExecutionId { get; set; }
    public long Sequence { get; set; }
    public string EventType { get; set; } = null!;
    public long OccurredAtUnixMilliseconds { get; set; }
    public string DataJson { get; set; } = null!;
}

public sealed class ExecutionWorkItemEntity
{
    public Guid ExecutionId { get; set; }
    public ExecutionWorkState State { get; set; }
    public long AvailableAtUnixMilliseconds { get; set; }
    public int DeliveryAttempt { get; set; }
    public string? LeaseOwner { get; set; }
    public Guid? LeaseToken { get; set; }
    public long? LeaseExpiresAtUnixMilliseconds { get; set; }
    public long? HeartbeatAtUnixMilliseconds { get; set; }
    public long? CompletedAtUnixMilliseconds { get; set; }
}

public sealed class GatewayCircuitEntity
{
    public string DeploymentId { get; set; } = null!;
    public GatewayCircuitState State { get; set; }
    public int ConsecutiveFailures { get; set; }
    public int OpenCount { get; set; }
    public long? OpenedAtUnixMilliseconds { get; set; }
    public long? NextProbeAtUnixMilliseconds { get; set; }
    public string? ProbeOwner { get; set; }
    public Guid? ProbeToken { get; set; }
    public long? ProbeLeaseExpiresAtUnixMilliseconds { get; set; }
    public GatewayFailureClass? LastFailureClass { get; set; }
    public long? LastFailureAtUnixMilliseconds { get; set; }
    public long UpdatedAtUnixMilliseconds { get; set; }
}

public sealed class WorkflowPlanEntity
{
    public Guid ExecutionId { get; set; }
    public string RequestJson { get; set; } = null!;
    public string PlanJson { get; set; } = null!;
    public string RoutingJson { get; set; } = null!;
    public long CreatedAtUnixMilliseconds { get; set; }
    public long UpdatedAtUnixMilliseconds { get; set; }
}

public sealed class WorkflowStepEntity
{
    public Guid ExecutionId { get; set; }
    public string StepId { get; set; } = null!;
    public WorkflowStepStatus Status { get; set; }
    public int Attempt { get; set; }
    public long? StartedAtUnixMilliseconds { get; set; }
    public long? CompletedAtUnixMilliseconds { get; set; }
    public string? OutputJson { get; set; }
    public string? ErrorJson { get; set; }
}

public sealed class ArtifactEntity
{
    public Guid ArtifactId { get; set; }
    public string TenantId { get; set; } = null!;
    public string ProjectId { get; set; } = string.Empty;
    public string TaskType { get; set; } = null!;
    public int TaskDefinitionVersion { get; set; }
    public string ArtifactType { get; set; } = null!;
    public int Version { get; set; }
    public string InputHash { get; set; } = null!;
    public string ContentHash { get; set; } = null!;
    public string ContentJson { get; set; } = null!;
    public string EvidenceJson { get; set; } = null!;
    public long CreatedAtUnixMilliseconds { get; set; }
    public long? ExpiresAtUnixMilliseconds { get; set; }
    public bool IsActive { get; set; }
    public string LogicalKey { get; set; } = null!;
    public ArtifactLifecycleStatus LifecycleStatus { get; set; }
    public Guid? SupersedesArtifactId { get; set; }
    public Guid? SupersededByArtifactId { get; set; }
    public long? InvalidatedAtUnixMilliseconds { get; set; }
    public string? InvalidationReason { get; set; }
}

public sealed class EfExecutionStore(
    IDbContextFactory<IRouteDbContext> contextFactory,
    IExecutionFence fence) : IExecutionStore
{
    /// <summary>
    /// Verifies that the lease in scope still owns the execution. Runs inside the caller's
    /// transaction so ownership cannot change between the check and the write.
    /// </summary>
    private async Task EnsureLeaseOwnsAsync(
        IRouteDbContext context,
        Guid executionId,
        CancellationToken cancellationToken)
    {
        if (fence.CurrentToken is not { } token)
        {
            return;
        }

        var owned = await context.ExecutionWorkItems
            .AsNoTracking()
            .AnyAsync(
                item => item.ExecutionId == executionId
                    && item.LeaseToken == token
                    && item.State == ExecutionWorkState.Leased,
                cancellationToken);
        if (!owned)
        {
            throw new LeaseFencedException(executionId);
        }
    }

    public async Task<ExecutionSubmission?> FindByIdempotencyKeyAsync(
        string tenantId,
        string key,
        CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var entity = await context.Executions
            .AsNoTracking()
            .SingleOrDefaultAsync(
                x => x.TenantId == tenantId && x.IdempotencyKey == key,
                cancellationToken);
        return entity is null
            ? null
            : new ExecutionSubmission(PersistenceMapping.ToContract(entity), entity.InputFingerprint);
    }

    public async Task<ExecutionSnapshot?> GetAsync(Guid executionId, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var entity = await context.Executions
            .AsNoTracking()
            .SingleOrDefaultAsync(x => x.ExecutionId == executionId, cancellationToken);
        return entity is null ? null : PersistenceMapping.ToContract(entity);
    }

    public async Task CreateAsync(
        ExecutionSnapshot execution,
        string? idempotencyKey,
        string? inputFingerprint,
        CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        context.Executions.Add(
            PersistenceMapping.ToEntity(execution, idempotencyKey, inputFingerprint));
        try
        {
            await context.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException exception)
            when (!string.IsNullOrWhiteSpace(idempotencyKey) && IsUniqueViolation(exception))
        {
            // A concurrent submit with the same key won the race. Surface a typed conflict so the
            // caller can re-read and answer with the execution that was actually created.
            throw new IdempotencyConflictException(execution.TenantId, idempotencyKey);
        }
    }

    private static bool IsUniqueViolation(DbUpdateException exception) =>
        exception.InnerException is { } inner &&
        (inner.Message.Contains("UNIQUE constraint failed", StringComparison.OrdinalIgnoreCase) ||
            inner.Message.Contains("duplicate key value", StringComparison.OrdinalIgnoreCase) ||
            (inner.GetType().Name == "PostgresException" &&
                inner.GetType().GetProperty("SqlState")?.GetValue(inner) as string == "23505"));

    public async Task UpdateAsync(ExecutionSnapshot execution, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
        await EnsureLeaseOwnsAsync(context, execution.ExecutionId, cancellationToken);
        var entity = await context.Executions.SingleAsync(
            x => x.ExecutionId == execution.ExecutionId,
            cancellationToken);
        PersistenceMapping.Apply(execution, entity);
        await context.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task<bool> TryRequestCancellationAsync(
        Guid executionId,
        DateTimeOffset requestedAt,
        CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

        // Touch only the cancellation column so a concurrent worker transition cannot be reverted
        // and a recorded outcome cannot be erased.
        var written = await context.Executions
            .Where(x => x.ExecutionId == executionId
                && x.CancellationRequestedAtUnixMilliseconds == null
                && !ExecutionStateMachine.TerminalStatuses.Contains(x.Status))
            .ExecuteUpdateAsync(
                setters => setters.SetProperty(
                    x => x.CancellationRequestedAtUnixMilliseconds,
                    requestedAt.ToUnixTimeMilliseconds()),
                cancellationToken);

        if (written > 0)
        {
            return true;
        }

        // Nothing was written: the execution is missing, already terminal, or already cancelled.
        var status = await context.Executions
            .AsNoTracking()
            .Where(x => x.ExecutionId == executionId)
            .Select(x => (ExecutionStatus?)x.Status)
            .SingleOrDefaultAsync(cancellationToken);

        return status is not null && !ExecutionStateMachine.IsTerminal(status.Value);
    }

    public async IAsyncEnumerable<ExecutionEvent> ReadEventsAsync(
        Guid executionId,
        long afterSequence,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var entities = await context.ExecutionEvents
            .AsNoTracking()
            .Where(x => x.ExecutionId == executionId && x.Sequence > afterSequence)
            .OrderBy(x => x.Sequence)
            .ToListAsync(cancellationToken);
        foreach (var entity in entities)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return PersistenceMapping.ToContract(entity);
        }
    }

    public async Task<ExecutionEvent> AppendEventAsync(
        Guid executionId,
        string eventType,
        DateTimeOffset occurredAt,
        JsonElement data,
        CancellationToken cancellationToken)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
                var isPostgres = context.Database.ProviderName?.Contains(
                    "Npgsql",
                    StringComparison.Ordinal) is true;
                await using var transaction = await context.Database.BeginTransactionAsync(
                    isPostgres ? IsolationLevel.ReadCommitted : IsolationLevel.Serializable,
                    cancellationToken);
                if (isPostgres)
                {
                    _ = await context.Executions
                        .FromSqlInterpolated(
                            $"SELECT * FROM \"Executions\" WHERE \"ExecutionId\" = {executionId} FOR UPDATE")
                        .AsNoTracking()
                        .SingleAsync(cancellationToken);
                }

                await EnsureLeaseOwnsAsync(context, executionId, cancellationToken);

                var previousSequence = await context.ExecutionEvents
                    .Where(x => x.ExecutionId == executionId)
                    .MaxAsync(x => (long?)x.Sequence, cancellationToken) ?? 0;
                var executionEvent = new ExecutionEvent(
                    checked(previousSequence + 1),
                    executionId,
                    eventType,
                    occurredAt,
                    data.Clone());
                context.ExecutionEvents.Add(PersistenceMapping.ToEntity(executionEvent));
                await context.SaveChangesAsync(cancellationToken);
                await transaction.CommitAsync(cancellationToken);
                return executionEvent;
            }
            catch (Exception exception) when (attempt < 5 && IsEventSequenceContention(exception))
            {
                await Task.Delay(TimeSpan.FromMilliseconds(attempt * 5), cancellationToken);
            }
        }
    }

    private static bool IsEventSequenceContention(Exception exception) => exception switch
    {
        DbUpdateException { InnerException: Npgsql.PostgresException { SqlState: "23505" or "40001" } } => true,
        Npgsql.PostgresException { SqlState: "23505" or "40001" } => true,
        Microsoft.Data.Sqlite.SqliteException { SqliteErrorCode: 5 or 6 or 19 } => true,
        _ => false
    };
}

public sealed class EfWorkflowCheckpointStore(IDbContextFactory<IRouteDbContext> contextFactory)
    : IWorkflowCheckpointStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

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

        return steps.Count;
    }

    public async Task<WorkflowStepCheckpoint> StartStepAsync(
        Guid executionId,
        string stepId,
        DateTimeOffset startedAt,
        CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
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
        var step = await GetRunningStepAsync(context, executionId, stepId, cancellationToken);
        step.Status = WorkflowStepStatus.Succeeded;
        step.OutputJson = output.GetRawText();
        step.CompletedAtUnixMilliseconds = completedAt.ToUnixTimeMilliseconds();
        step.ErrorJson = null;
        await TouchPlanAsync(context, executionId, completedAt, cancellationToken);
        await context.SaveChangesAsync(cancellationToken);
    }

    public async Task ResetStepForRetryAsync(
        Guid executionId,
        string stepId,
        DateTimeOffset updatedAt,
        CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var step = await GetRunningStepAsync(context, executionId, stepId, cancellationToken);
        step.Status = WorkflowStepStatus.Pending;
        step.StartedAtUnixMilliseconds = null;
        step.CompletedAtUnixMilliseconds = null;
        step.ErrorJson = null;
        await TouchPlanAsync(context, executionId, updatedAt, cancellationToken);
        await context.SaveChangesAsync(cancellationToken);
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
        var step = await GetStepAsync(context, executionId, stepId, cancellationToken);
        if (step.Status == WorkflowStepStatus.Succeeded)
        {
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
    }

    public async Task CancelIncompleteStepsAsync(
        Guid executionId,
        Problem problem,
        DateTimeOffset completedAt,
        CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
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
}

public sealed class EfArtifactStore(
    IDbContextFactory<IRouteDbContext> contextFactory,
    LifecyclePolicy? lifecyclePolicy = null) : IArtifactStore
{
    private readonly LifecyclePolicy _lifecyclePolicy = lifecyclePolicy ?? new LifecyclePolicy();

    public async Task<ArtifactRecord?> FindReusableAsync(
        ArtifactReuseQuery query,
        CancellationToken cancellationToken)
    {
        var projectId = query.ProjectId ?? string.Empty;
        var now = query.At.ToUnixTimeMilliseconds();
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var entity = await context.Artifacts
            .AsNoTracking()
            .Where(x =>
                x.TenantId == query.TenantId &&
                x.ProjectId == projectId &&
                x.TaskType == query.TaskType &&
                x.TaskDefinitionVersion == query.TaskDefinitionVersion &&
                x.InputHash == query.InputHash &&
                x.LogicalKey == query.LogicalKey &&
                x.IsActive &&
                x.LifecycleStatus == ArtifactLifecycleStatus.Active &&
                (x.ExpiresAtUnixMilliseconds == null || x.ExpiresAtUnixMilliseconds > now))
            .OrderByDescending(x => x.Version)
            .FirstOrDefaultAsync(cancellationToken);
        return entity is null ? null : await ToRecordAsync(context, entity, cancellationToken);
    }

    public async Task<ArtifactRecord?> GetAsync(
        string tenantId,
        Guid artifactId,
        CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var entity = await context.Artifacts
            .AsNoTracking()
            .SingleOrDefaultAsync(
                x => x.TenantId == tenantId && x.ArtifactId == artifactId,
                cancellationToken);
        return entity is null ? null : await ToRecordAsync(context, entity, cancellationToken);
    }

    public async Task<ArtifactRecord?> FindActiveAsync(
        ArtifactLookupQuery query,
        CancellationToken cancellationToken)
    {
        var projectId = query.ProjectId ?? string.Empty;
        var now = query.At.ToUnixTimeMilliseconds();
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var entity = await context.Artifacts
            .AsNoTracking()
            .Where(item =>
                item.TenantId == query.TenantId &&
                item.ProjectId == projectId &&
                item.TaskType == query.TaskType &&
                item.TaskDefinitionVersion == query.TaskDefinitionVersion &&
                item.ArtifactType == query.ArtifactType &&
                item.LogicalKey == query.LogicalKey &&
                item.IsActive &&
                item.LifecycleStatus == ArtifactLifecycleStatus.Active &&
                (item.ExpiresAtUnixMilliseconds == null || item.ExpiresAtUnixMilliseconds > now))
            .OrderByDescending(item => item.Version)
            .ThenByDescending(item => item.ArtifactId)
            .FirstOrDefaultAsync(cancellationToken);
        return entity is null ? null : await ToRecordAsync(context, entity, cancellationToken);
    }

    public async Task<ArtifactRecord> SaveAsync(
        ArtifactRecord artifact,
        CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await context.Database.BeginTransactionAsync(
            IsolationLevel.Serializable,
            cancellationToken);
        var projectId = artifact.ProjectId ?? string.Empty;
        var logicalKey = artifact.EffectiveLogicalKey;
        var lineage = await context.Artifacts
            .Where(x =>
                x.TenantId == artifact.TenantId &&
                x.ProjectId == projectId &&
                x.ArtifactType == artifact.ArtifactType &&
                x.LogicalKey == logicalKey)
            .OrderByDescending(x => x.Version)
            .ThenByDescending(x => x.ArtifactId)
            .ToListAsync(cancellationToken);
        var active = lineage.FirstOrDefault(item =>
            item.IsActive && item.LifecycleStatus == ArtifactLifecycleStatus.Active);
        if (active is not null &&
            string.Equals(active.InputHash, artifact.InputHash, StringComparison.Ordinal) &&
            string.Equals(active.ContentHash, artifact.ContentHash, StringComparison.Ordinal) &&
            (active.ExpiresAtUnixMilliseconds is null ||
                active.ExpiresAtUnixMilliseconds > artifact.CreatedAt.ToUnixTimeMilliseconds()))
        {
            var existing = await ToRecordAsync(context, active, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return existing;
        }

        var previous = lineage.FirstOrDefault();
        var versioned = artifact with
        {
            Version = checked((previous?.Version ?? 0) + 1),
            LogicalKey = logicalKey,
            IsActive = true,
            LifecycleStatus = ArtifactLifecycleStatus.Active,
            SupersedesArtifactId = previous?.ArtifactId,
            SupersededByArtifactId = null,
            Dependencies = EfMemoryStore.NormalizeDependencies(artifact.EffectiveDependencies),
            InvalidatedAt = null,
            InvalidationReason = null,
            ExpiresAt = artifact.ExpiresAt ??
                artifact.CreatedAt.Add(_lifecyclePolicy.DefaultArtifactTimeToLive)
        };
        if (active is not null)
        {
            active.IsActive = false;
            active.LifecycleStatus = ArtifactLifecycleStatus.Superseded;
            active.SupersededByArtifactId = versioned.ArtifactId;
        }

        context.Artifacts.Add(PersistenceMapping.ToEntity(versioned));
        EfMemoryStore.AddDependencyEdges(
            context,
            "artifact",
            versioned.ArtifactId,
            versioned.TenantId,
            versioned.EffectiveDependencies);
        await context.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return versioned;
    }

    public async Task<ArtifactInvalidationResult> InvalidateByDependencyAsync(
        DependencyChange change,
        CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await context.Database.BeginTransactionAsync(
            IsolationLevel.Serializable,
            cancellationToken);
        var invalidated = new List<Guid>();
        var seen = new HashSet<Guid>();
        var changes = new Queue<DependencyChange>();
        changes.Enqueue(change);
        while (changes.TryDequeue(out var current))
        {
            var sourceIds = await EfMemoryStore.MatchingSourceIdsAsync(
                context,
                current,
                "artifact",
                cancellationToken);
            var candidates = await context.Artifacts
                .Where(item =>
                    item.TenantId == change.TenantId &&
                    sourceIds.Contains(item.ArtifactId) &&
                    item.IsActive &&
                    item.LifecycleStatus == ArtifactLifecycleStatus.Active)
                .OrderBy(item => item.Version)
                .ThenBy(item => item.ArtifactId)
                .ToListAsync(cancellationToken);
            foreach (var candidate in candidates.Where(item => seen.Add(item.ArtifactId)))
            {
                candidate.IsActive = false;
                candidate.LifecycleStatus = ArtifactLifecycleStatus.Invalidated;
                candidate.InvalidatedAtUnixMilliseconds = change.OccurredAt.ToUnixTimeMilliseconds();
                candidate.InvalidationReason = change.Reason;
                invalidated.Add(candidate.ArtifactId);
                changes.Enqueue(new DependencyChange(
                    change.TenantId,
                    "artifact",
                    candidate.ArtifactId.ToString(),
                    candidate.ContentHash,
                    true,
                    change.Reason,
                    change.OccurredAt));
            }
        }

        await context.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new ArtifactInvalidationResult(invalidated);
    }

    private static async Task<ArtifactRecord> ToRecordAsync(
        IRouteDbContext context,
        ArtifactEntity entity,
        CancellationToken cancellationToken) =>
        PersistenceMapping.ToContract(
            entity,
            await EfMemoryStore.ReadDependenciesAsync(
                context,
                "artifact",
                entity.ArtifactId,
                cancellationToken));
}

public sealed partial class PersistenceInitializer(
    IDbContextFactory<IRouteDbContext> contextFactory,
    IOptions<StorageOptions> storageOptions,
    StorageProvider storageProvider,
    IHostEnvironment environment,
    ILogger<PersistenceInitializer> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (!storageProvider.SupportsMultipleWorkers && !environment.IsDevelopment())
        {
            // Not fatal: a single-node self-host is a legitimate way to run iRoute. It is logged
            // because the limit is invisible until a second worker silently contends for the file.
            LogSingleNodeStorage(logger, storageProvider.Name, environment.EnvironmentName);
        }

        if (!storageOptions.Value.AutoInitialize)
        {
            return;
        }

        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        await context.Database.MigrateAsync(cancellationToken);
    }

    [LoggerMessage(
        1,
        LogLevel.Warning,
        "Storage:Provider={Provider} runs on one node only and cannot coordinate multiple execution " +
        "workers, but the environment is {Environment}. Use Postgres to deploy.")]
    private static partial void LogSingleNodeStorage(
        ILogger logger,
        string provider,
        string environment);

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

public sealed class DurableStorageHealthCheck(
    IDbContextFactory<IRouteDbContext> contextFactory) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await using var database = await contextFactory.CreateDbContextAsync(cancellationToken);
            if (!await database.Database.CanConnectAsync(cancellationToken))
            {
                return HealthCheckResult.Unhealthy("The durable store is not reachable.");
            }

            var pendingMigrations = await database.Database
                .GetPendingMigrationsAsync(cancellationToken);
            return pendingMigrations.Any()
                ? HealthCheckResult.Unhealthy(
                    "The durable store is reachable but requires schema migrations.")
                : HealthCheckResult.Healthy("The durable store is reachable and schema-current.");
        }
        catch (Exception exception)
        {
            return HealthCheckResult.Unhealthy("The durable store health check failed.", exception);
        }
    }
}

internal static class PersistenceMapping
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static ExecutionEntity ToEntity(
        ExecutionSnapshot snapshot,
        string? idempotencyKey,
        string? inputFingerprint) => new()
    {
        ExecutionId = snapshot.ExecutionId,
        TenantId = snapshot.TenantId,
        ActorId = snapshot.ActorId,
        ProjectId = snapshot.ProjectId,
        TaskType = snapshot.TaskType,
        TaskDefinitionVersion = snapshot.TaskDefinitionVersion,
        Status = snapshot.Status,
        CreatedAtUnixMilliseconds = snapshot.CreatedAt.ToUnixTimeMilliseconds(),
        UpdatedAtUnixMilliseconds = snapshot.UpdatedAt.ToUnixTimeMilliseconds(),
        CancellationRequestedAtUnixMilliseconds = snapshot.CancellationRequestedAt?.ToUnixTimeMilliseconds(),
        IdempotencyKey = idempotencyKey,
        InputFingerprint = inputFingerprint,
        OutcomeJson = Serialize(snapshot.Outcome),
        ErrorJson = Serialize(snapshot.Error)
    };

    public static void Apply(ExecutionSnapshot snapshot, ExecutionEntity entity)
    {
        entity.TenantId = snapshot.TenantId;
        entity.ActorId = snapshot.ActorId;
        entity.ProjectId = snapshot.ProjectId;
        entity.TaskType = snapshot.TaskType;
        entity.TaskDefinitionVersion = snapshot.TaskDefinitionVersion;
        entity.Status = snapshot.Status;
        entity.UpdatedAtUnixMilliseconds = snapshot.UpdatedAt.ToUnixTimeMilliseconds();
        // CancellationRequestedAt is deliberately not written here: it is owned by
        // TryRequestCancellationAsync so a stale snapshot cannot erase a cancellation request.
        entity.OutcomeJson = Serialize(snapshot.Outcome);
        entity.ErrorJson = Serialize(snapshot.Error);
    }

    public static ExecutionSnapshot ToContract(ExecutionEntity entity) => new(
        entity.ExecutionId,
        entity.TaskType,
        entity.Status,
        DateTimeOffset.FromUnixTimeMilliseconds(entity.CreatedAtUnixMilliseconds),
        DateTimeOffset.FromUnixTimeMilliseconds(entity.UpdatedAtUnixMilliseconds),
        Deserialize<TaskOutcome>(entity.OutcomeJson),
        Deserialize<Problem>(entity.ErrorJson),
        entity.TenantId,
        entity.ActorId,
        entity.ProjectId,
        entity.TaskDefinitionVersion,
        entity.CancellationRequestedAtUnixMilliseconds is { } cancellationRequestedAt
            ? DateTimeOffset.FromUnixTimeMilliseconds(cancellationRequestedAt)
            : null);

    public static ExecutionEventEntity ToEntity(ExecutionEvent executionEvent) => new()
    {
        ExecutionId = executionEvent.ExecutionId,
        Sequence = executionEvent.Sequence,
        EventType = executionEvent.Type,
        OccurredAtUnixMilliseconds = executionEvent.OccurredAt.ToUnixTimeMilliseconds(),
        DataJson = executionEvent.Data.GetRawText()
    };

    public static ExecutionEvent ToContract(ExecutionEventEntity entity) => new(
        entity.Sequence,
        entity.ExecutionId,
        entity.EventType,
        DateTimeOffset.FromUnixTimeMilliseconds(entity.OccurredAtUnixMilliseconds),
        JsonSerializer.Deserialize<JsonElement>(entity.DataJson, JsonOptions));

    public static ArtifactEntity ToEntity(ArtifactRecord artifact) => new()
    {
        ArtifactId = artifact.ArtifactId,
        TenantId = artifact.TenantId,
        ProjectId = artifact.ProjectId ?? string.Empty,
        TaskType = artifact.TaskType,
        TaskDefinitionVersion = artifact.TaskDefinitionVersion,
        ArtifactType = artifact.ArtifactType,
        Version = artifact.Version,
        InputHash = artifact.InputHash,
        ContentHash = artifact.ContentHash,
        ContentJson = artifact.Content.GetRawText(),
        EvidenceJson = JsonSerializer.Serialize(artifact.Evidence, JsonOptions),
        CreatedAtUnixMilliseconds = artifact.CreatedAt.ToUnixTimeMilliseconds(),
        ExpiresAtUnixMilliseconds = artifact.ExpiresAt?.ToUnixTimeMilliseconds(),
        IsActive = artifact.IsActive,
        LogicalKey = artifact.EffectiveLogicalKey,
        LifecycleStatus = artifact.LifecycleStatus,
        SupersedesArtifactId = artifact.SupersedesArtifactId,
        SupersededByArtifactId = artifact.SupersededByArtifactId,
        InvalidatedAtUnixMilliseconds = artifact.InvalidatedAt?.ToUnixTimeMilliseconds(),
        InvalidationReason = artifact.InvalidationReason
    };

    public static ArtifactRecord ToContract(
        ArtifactEntity entity,
        IReadOnlyList<DependencyReference>? dependencies = null) => new(
        entity.ArtifactId,
        entity.TenantId,
        string.IsNullOrEmpty(entity.ProjectId) ? null : entity.ProjectId,
        entity.TaskType,
        entity.TaskDefinitionVersion,
        entity.ArtifactType,
        entity.Version,
        entity.InputHash,
        entity.ContentHash,
        JsonSerializer.Deserialize<JsonElement>(entity.ContentJson, JsonOptions),
        JsonSerializer.Deserialize<EvidenceReference[]>(entity.EvidenceJson, JsonOptions) ?? [],
        DateTimeOffset.FromUnixTimeMilliseconds(entity.CreatedAtUnixMilliseconds),
        entity.ExpiresAtUnixMilliseconds is { } expiresAt
            ? DateTimeOffset.FromUnixTimeMilliseconds(expiresAt)
            : null,
        entity.IsActive,
        entity.LogicalKey,
        entity.LifecycleStatus,
        entity.SupersedesArtifactId,
        entity.SupersededByArtifactId,
        dependencies ?? [],
        entity.InvalidatedAtUnixMilliseconds is { } invalidatedAt
            ? DateTimeOffset.FromUnixTimeMilliseconds(invalidatedAt)
            : null,
        entity.InvalidationReason);

    private static string? Serialize<T>(T? value) where T : class =>
        value is null ? null : JsonSerializer.Serialize(value, JsonOptions);

    private static T? Deserialize<T>(string? value) where T : class =>
        value is null ? null : JsonSerializer.Deserialize<T>(value, JsonOptions);
}
