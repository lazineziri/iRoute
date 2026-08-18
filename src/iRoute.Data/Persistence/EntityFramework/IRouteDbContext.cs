using Microsoft.EntityFrameworkCore;

namespace iRoute.Data;

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
