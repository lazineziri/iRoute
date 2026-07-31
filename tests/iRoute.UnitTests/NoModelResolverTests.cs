using System.Text.Json;
using iRoute.Contracts;
using iRoute.Core;
using iRoute.Infrastructure;
using iRoute.Runtime;

namespace iRoute.UnitTests;

public sealed class NoModelResolverTests
{
    private static readonly string[] ProjectReadScope = ["project:read"];

    [Fact]
    public async Task FactDecisionResolverEnforcesPermissionScopeTenantAndFreshness()
    {
        var memories = new InMemoryMemoryStore();
        var clock = new SystemClock();
        var resolver = new FactDecisionResolver(memories, clock);
        var definition = await FindDefinitionAsync("project.decision.get");
        var active = await memories.UpsertAsync(
            CreateMemory("tenant-a", "project-1", "architecture", "Use PostgreSQL", clock.UtcNow.AddHours(1)),
            TestContext.Current.CancellationToken);

        var accepted = await resolver.ResolveAsync(
            CreateStateRequest("tenant-a", "architecture", ProjectReadScope),
            definition,
            TestContext.Current.CancellationToken);
        var wrongTenant = await resolver.ResolveAsync(
            CreateStateRequest("tenant-b", "architecture", ProjectReadScope),
            definition,
            TestContext.Current.CancellationToken);
        var denied = await resolver.ResolveAsync(
            CreateStateRequest("tenant-a", "architecture", []),
            definition,
            TestContext.Current.CancellationToken);

        Assert.True(accepted.Accepted);
        Assert.Equal(ResolutionDecisionCodes.StateHit, accepted.Code);
        Assert.Equal(ResolutionLevel.StructuredState, accepted.Candidate!.Level);
        Assert.Equal(active.Record.MemoryId.ToString(), Assert.Single(
            accepted.Candidate.Evidence,
            item => item.Kind == "memory").Reference);
        Assert.False(wrongTenant.Accepted);
        Assert.Equal(ResolutionDecisionCodes.StateUnavailable, wrongTenant.Code);
        Assert.True(wrongTenant.FreshnessChecked);
        Assert.False(denied.Accepted);
        Assert.Equal(ResolutionDecisionCodes.PermissionDenied, denied.Code);
        Assert.True(denied.PermissionChecked);
        Assert.False(denied.FreshnessChecked);

        await memories.UpsertAsync(
            CreateMemory("tenant-a", "project-1", "stale", "Old decision", clock.UtcNow.AddMinutes(-1)),
            TestContext.Current.CancellationToken);
        var stale = await resolver.ResolveAsync(
            CreateStateRequest("tenant-a", "stale", ProjectReadScope),
            definition,
            TestContext.Current.CancellationToken);
        Assert.False(stale.Accepted);
        Assert.Equal(ResolutionDecisionCodes.StateUnavailable, stale.Code);
        Assert.True(stale.FreshnessChecked);
    }

    [Fact]
    public async Task ExactResultResolverIncludesLogicalKeyInExactCacheIdentity()
    {
        var artifacts = new InMemoryArtifactStore();
        var fingerprint = new Sha256InputFingerprint();
        var clock = new SystemClock();
        var resolver = new ExactResultResolver(artifacts, fingerprint, clock);
        var definition = await FindDefinitionAsync("email.draft");
        var request = new TaskRequest(
            "email.draft",
            JsonSerializer.SerializeToElement(new { objective = "Same input" }),
            ProjectId: "project-1",
            Metadata: new Dictionary<string, string> { ["artifactKey"] = "draft-b" },
            TenantId: "tenant-a",
            ActorId: "actor-a");
        var inputHash = fingerprint.Create(request, definition.Version);
        await artifacts.SaveAsync(
            CreateArtifact(
                "tenant-a",
                "project-1",
                "draft-a",
                inputHash,
                "Draft A",
                clock.UtcNow.AddHours(1)),
            TestContext.Current.CancellationToken);
        var expected = await artifacts.SaveAsync(
            CreateArtifact(
                "tenant-a",
                "project-1",
                "draft-b",
                inputHash,
                "Draft B",
                clock.UtcNow.AddHours(1)),
            TestContext.Current.CancellationToken);
        await artifacts.SaveAsync(
            CreateArtifact(
                "tenant-a",
                "project-1",
                "draft-expired",
                inputHash,
                "Expired draft",
                clock.UtcNow.AddMinutes(-1)),
            TestContext.Current.CancellationToken);

        var decision = await resolver.ResolveAsync(
            request,
            definition,
            TestContext.Current.CancellationToken);
        var expired = await resolver.ResolveAsync(
            request with
            {
                Metadata = new Dictionary<string, string> { ["artifactKey"] = "draft-expired" }
            },
            definition,
            TestContext.Current.CancellationToken);

        Assert.True(decision.Accepted);
        Assert.Equal(ResolutionDecisionCodes.ExactCacheHit, decision.Code);
        Assert.Equal(expected.ArtifactId, decision.Candidate!.Artifact!.ArtifactId);
        Assert.Equal("Draft B", decision.Candidate.Output.GetProperty("body").GetString());
        Assert.False(expired.Accepted);
        Assert.Equal(ResolutionDecisionCodes.ExactCacheMiss, expired.Code);
        Assert.True(expired.FreshnessChecked);
    }

    [Fact]
    public async Task ArtifactLookupResolverRejectsStaleAndOutOfProjectArtifacts()
    {
        var artifacts = new InMemoryArtifactStore();
        var clock = new SystemClock();
        var resolver = new ArtifactLookupResolver(artifacts, clock);
        var definition = await FindDefinitionAsync("email.draft");
        var active = await artifacts.SaveAsync(
            CreateArtifact(
                "tenant-a",
                "project-1",
                "active",
                "input-active",
                "Active draft",
                clock.UtcNow.AddHours(1)),
            TestContext.Current.CancellationToken);
        var stale = await artifacts.SaveAsync(
            CreateArtifact(
                "tenant-a",
                "project-1",
                "stale",
                "input-stale",
                "Stale draft",
                clock.UtcNow.AddMinutes(-1)),
            TestContext.Current.CancellationToken);

        var accepted = await resolver.ResolveAsync(
            CreateArtifactRequest("tenant-a", "project-1", active.ArtifactId),
            definition,
            TestContext.Current.CancellationToken);
        var byLogicalKey = await resolver.ResolveAsync(
            new TaskRequest(
                "email.draft",
                JsonSerializer.SerializeToElement(new { artifactKey = "active" }),
                ProjectId: "project-1",
                TenantId: "tenant-a",
                ActorId: "actor-a"),
            definition,
            TestContext.Current.CancellationToken);
        var wrongProject = await resolver.ResolveAsync(
            CreateArtifactRequest("tenant-a", "project-2", active.ArtifactId),
            definition,
            TestContext.Current.CancellationToken);
        var expired = await resolver.ResolveAsync(
            CreateArtifactRequest("tenant-a", "project-1", stale.ArtifactId),
            definition,
            TestContext.Current.CancellationToken);

        Assert.True(accepted.Accepted);
        Assert.Equal(active.ArtifactId, accepted.Candidate!.Artifact!.ArtifactId);
        Assert.True(byLogicalKey.Accepted);
        Assert.Equal(active.ArtifactId, byLogicalKey.Candidate!.Artifact!.ArtifactId);
        Assert.Equal(ResolutionDecisionCodes.ArtifactUnavailable, wrongProject.Code);
        Assert.Equal(ResolutionDecisionCodes.ArtifactUnavailable, expired.Code);
        Assert.True(wrongProject.FreshnessChecked);
        Assert.True(expired.FreshnessChecked);
    }

    [Fact]
    public async Task DeterministicHandlerResolverChecksPermissionAndResultFreshness()
    {
        var clock = new SystemClock();
        var handler = new StubDeterministicHandler(clock.UtcNow.AddMinutes(5));
        var resolver = new DeterministicHandlerResolver([handler], clock);
        var definition = new TaskDefinition(
            "deterministic.test",
            1,
            "deterministic.test",
            100,
            0.9m,
            true,
            SideEffectClass.None,
            "deterministic.test",
            DefaultMaxModelCalls: 0,
            AllowedCapabilities: ["deterministic.test"],
            PermissionScopes: ["test:read"]);
        var request = new TaskRequest(
            definition.TaskType,
            JsonSerializer.SerializeToElement(new { value = 42 }),
            TenantId: "tenant-a",
            PermissionScopes: ["test:read"]);

        var accepted = await resolver.ResolveAsync(
            request,
            definition,
            TestContext.Current.CancellationToken);
        var denied = await resolver.ResolveAsync(
            request with { PermissionScopes = [] },
            definition,
            TestContext.Current.CancellationToken);
        var staleHandler = new StubDeterministicHandler(clock.UtcNow.AddMinutes(-1));
        var stale = await new DeterministicHandlerResolver([staleHandler], clock).ResolveAsync(
            request,
            definition,
            TestContext.Current.CancellationToken);
        var capabilityDenied = await resolver.ResolveAsync(
            request,
            definition with { AllowedCapabilities = ["different.capability"] },
            TestContext.Current.CancellationToken);

        Assert.True(accepted.Accepted);
        Assert.Equal(ResolutionDecisionCodes.HandlerAccepted, accepted.Code);
        Assert.Equal(ResolutionLevel.DeterministicCapability, accepted.Candidate!.Level);
        Assert.Equal(1, accepted.Candidate.Usage!.ToolCalls);
        Assert.Contains(accepted.Candidate.Evidence, item => item.Kind == "deterministic-handler");
        Assert.Equal(ResolutionDecisionCodes.PermissionDenied, denied.Code);
        Assert.Equal(1, handler.InvocationCount);
        Assert.Equal(ResolutionDecisionCodes.HandlerStale, stale.Code);
        Assert.True(stale.FreshnessChecked);
        Assert.Equal(ResolutionDecisionCodes.HandlerUnavailable, capabilityDenied.Code);
        Assert.Equal(1, handler.InvocationCount);
    }

    private static async Task<TaskDefinition> FindDefinitionAsync(string taskType) =>
        await new BuiltInTaskDefinitionRegistry().FindAsync(
            taskType,
            TestContext.Current.CancellationToken)
        ?? throw new InvalidOperationException($"Task definition '{taskType}' is missing.");

    private static TaskRequest CreateStateRequest(
        string tenantId,
        string key,
        IReadOnlyList<string> permissionScopes) => new(
        "project.decision.get",
        JsonSerializer.SerializeToElement(new { key }),
        ProjectId: "project-1",
        TenantId: tenantId,
        ActorId: "actor-a",
        PermissionScopes: permissionScopes);

    private static TaskRequest CreateArtifactRequest(
        string tenantId,
        string projectId,
        Guid artifactId) => new(
        "email.draft",
        JsonSerializer.SerializeToElement(new { artifactId }),
        ProjectId: projectId,
        TenantId: tenantId,
        ActorId: "actor-a");

    private static MemoryRecord CreateMemory(
        string tenantId,
        string projectId,
        string key,
        string value,
        DateTimeOffset expiresAt)
    {
        var content = JsonSerializer.SerializeToElement(value);
        return new MemoryRecord(
            Guid.CreateVersion7(),
            tenantId,
            projectId,
            MemoryKind.Decision,
            key,
            1,
            content,
            $"hash-{key}",
            MemoryLifecycleStatus.Active,
            [new EvidenceReference("request", $"input.{key}")],
            [],
            DateTimeOffset.UtcNow,
            expiresAt);
    }

    private static ArtifactRecord CreateArtifact(
        string tenantId,
        string projectId,
        string logicalKey,
        string inputHash,
        string body,
        DateTimeOffset expiresAt)
    {
        var content = JsonSerializer.SerializeToElement(new { subject = "Update", body });
        return new ArtifactRecord(
            Guid.CreateVersion7(),
            tenantId,
            projectId,
            "email.draft",
            1,
            "email.draft",
            1,
            inputHash,
            $"hash-{logicalKey}",
            content,
            [new EvidenceReference("request", "input")],
            DateTimeOffset.UtcNow,
            expiresAt,
            true,
            logicalKey);
    }

    private sealed class StubDeterministicHandler(DateTimeOffset expiresAt) : IDeterministicTaskHandler
    {
        public string Name => "stub-handler";
        public string Capability => "deterministic.test";
        public int InvocationCount { get; private set; }

        public bool Supports(TaskDefinition definition) => definition.TaskType == "deterministic.test";

        public Task<DeterministicHandlerResult?> TryResolveAsync(
            TaskRequest request,
            TaskDefinition definition,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            InvocationCount++;
            return Task.FromResult<DeterministicHandlerResult?>(new(
                JsonSerializer.SerializeToElement(new { result = 42 }),
                1m,
                [new EvidenceReference("fixture", "deterministic")],
                expiresAt,
                ["The fixture input was resolved deterministically."]));
        }
    }
}
