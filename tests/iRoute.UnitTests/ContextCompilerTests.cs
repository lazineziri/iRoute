using System.Text.Json;
using iRoute.Contracts;
using iRoute.Core;
using iRoute.Infrastructure;
using iRoute.Runtime;

namespace iRoute.UnitTests;

public sealed class ContextCompilerTests
{
    private static readonly string[] BoundedDecisions = ["Keep context bounded."];
    private static readonly string[] SubjectSection = ["subject"];

    [Fact]
    public async Task CompilerRanksSourcesFiltersSupersededAndDuplicateStateAndCapsHistory()
    {
        var memories = new InMemoryMemoryStore();
        var artifacts = new InMemoryArtifactStore();
        var clock = new SystemClock();
        await memories.UpsertAsync(
            CreateMemory(
                "tenant-a",
                "project-1",
                MemoryKind.Decision,
                "architecture",
                "Use the superseded SQLite decision.",
                clock.UtcNow),
            TestContext.Current.CancellationToken);
        var compiler = new BoundedContextCompiler(memories, artifacts, clock);
        var definition = await FindDefinitionAsync("email.draft");
        var request = new TaskRequest(
            "email.draft",
            JsonSerializer.SerializeToElement(new
            {
                objective = "Draft a PostgreSQL architecture update.",
                activeDecisions = new object[]
                {
                    new { key = "architecture", value = "Use PostgreSQL for production." },
                    new { key = "duplicate", value = "Use the signed architecture decision." }
                },
                authoritativeSources = new object[]
                {
                    new { reference = "adr:001", value = "Use the signed architecture decision." },
                    new { reference = "spec:runtime", value = "The runtime must preserve provenance." }
                },
                projectHistory = Enumerable.Range(0, 6)
                    .Select(index => $"Project history item {index} about the runtime.")
                    .ToArray()
            }),
            ProjectId: "project-1",
            Constraints: new TaskConstraints(MaxInputTokens: 1000),
            TenantId: "tenant-a");

        var compiled = await compiler.CompileAsync(
            request,
            definition,
            TestContext.Current.CancellationToken);

        Assert.False(compiled.Manifest.FullHistoryIncluded);
        Assert.False(compiled.Manifest.EstimatedTokens > compiled.Manifest.BudgetTokens);
        Assert.Equal(
            compiled.Manifest.EstimatedTokens,
            compiled.Manifest.ProjectedInputTokens + compiled.Manifest.ContextTokens);
        Assert.True(compiled.ProjectedInput.TryGetProperty("objective", out _));
        Assert.False(compiled.ProjectedInput.TryGetProperty("activeDecisions", out _));
        Assert.False(compiled.ProjectedInput.TryGetProperty("authoritativeSources", out _));
        Assert.False(compiled.ProjectedInput.TryGetProperty("projectHistory", out _));
        var decisions = compiled.Content.GetProperty("activeDecisions").EnumerateArray().ToArray();
        Assert.Contains(decisions, item => item.GetString() == "Use PostgreSQL for production.");
        Assert.DoesNotContain(decisions, item => item.GetString() == "Use the superseded SQLite decision.");
        Assert.Equal(3, compiled.Content.GetProperty("projectHistory").GetArrayLength());
        Assert.Contains(compiled.Manifest.Entries, entry =>
            !entry.Included && entry.Reason.Contains("superseded", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(compiled.Manifest.Entries, entry =>
            !entry.Included && entry.Reason.Contains("exact duplicate", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(3, compiled.Manifest.Entries.Count(entry =>
            !entry.Included && entry.Reason.Contains("full history", StringComparison.OrdinalIgnoreCase)));
        var included = compiled.Manifest.Entries.Where(entry => entry.Included).ToArray();
        Assert.All(included, entry =>
        {
            Assert.NotNull(entry.OutputPath);
            Assert.True(compiled.Manifest.Provenance.ContainsKey(entry.OutputPath));
            Assert.False(string.IsNullOrWhiteSpace(
                compiled.Manifest.Provenance[entry.OutputPath].Reference));
        });
        Assert.Equal(included.Length, compiled.Manifest.Provenance.Count);
        Assert.Equal(compiled.Manifest.Provenance.Count, compiled.Evidence.Count);
        Assert.Equal(
            included.Length,
            included.Select(entry => entry.ContentHash).Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public async Task CompilerMeasuresTheCompleteSerializedPackageAgainstTheBudget()
    {
        var compiler = new BoundedContextCompiler(
            new InMemoryMemoryStore(),
            new InMemoryArtifactStore(),
            new SystemClock());
        var definition = await FindDefinitionAsync("email.draft");
        var request = new TaskRequest(
            "email.draft",
            JsonSerializer.SerializeToElement(new
            {
                activeDecisions = BoundedDecisions,
                authoritativeSources = Enumerable.Range(0, 8)
                    .Select(index => new
                    {
                        reference = $"source:{index}",
                        value = new string((char)('a' + index), 240)
                    })
                    .ToArray()
            }),
            ProjectId: "project-1",
            Constraints: new TaskConstraints(MaxInputTokens: 40),
            TenantId: "tenant-a");

        var compiled = await compiler.CompileAsync(
            request,
            definition,
            TestContext.Current.CancellationToken);

        Assert.InRange(compiled.Manifest.EstimatedTokens, 1, 40);
        Assert.Equal(40, compiled.Manifest.BudgetTokens);
        Assert.Equal(
            compiled.Manifest.EstimatedTokens,
            compiled.Manifest.ProjectedInputTokens + compiled.Manifest.ContextTokens);
        Assert.True(compiled.Manifest.Truncated);
        Assert.Contains(compiled.Manifest.Entries, entry =>
            !entry.Included && entry.Reason.Contains("token budget", StringComparison.OrdinalIgnoreCase));
        Assert.All(
            compiled.Manifest.Entries.Where(entry => entry.Included),
            entry => Assert.True(compiled.Manifest.Provenance.ContainsKey(entry.OutputPath!)));
    }

    [Fact]
    public async Task CompilerProjectsOnlyRequestedSectionsFromFreshScopedArtifacts()
    {
        var artifacts = new InMemoryArtifactStore();
        var clock = new SystemClock();
        var active = await artifacts.SaveAsync(
            CreateArtifact(
                "tenant-a",
                "project-1",
                JsonSerializer.SerializeToElement(new
                {
                    subject = "Prior subject",
                    body = "Prior body that must not be included.",
                    privateNotes = "Internal notes that must not be included."
                }),
                clock.UtcNow.AddHours(1)),
            TestContext.Current.CancellationToken);
        var wrongProject = await artifacts.SaveAsync(
            CreateArtifact(
                "tenant-a",
                "project-2",
                JsonSerializer.SerializeToElement(new { subject = "Wrong project" }),
                clock.UtcNow.AddHours(1)),
            TestContext.Current.CancellationToken);
        var compiler = new BoundedContextCompiler(new InMemoryMemoryStore(), artifacts, clock);
        var definition = await FindDefinitionAsync("email.draft");
        var request = new TaskRequest(
            "email.draft",
            JsonSerializer.SerializeToElement(new
            {
                objective = "Draft a new update.",
                contextArtifacts = new object[]
                {
                    new { artifactId = active.ArtifactId, sections = SubjectSection },
                    new { artifactId = wrongProject.ArtifactId, sections = SubjectSection }
                }
            }),
            ProjectId: "project-1",
            TenantId: "tenant-a");

        var compiled = await compiler.CompileAsync(
            request,
            definition,
            TestContext.Current.CancellationToken);

        var slices = compiled.Content.GetProperty("artifacts").EnumerateArray().ToArray();
        Assert.Single(slices);
        Assert.Equal("Prior subject", slices[0].GetString());
        Assert.DoesNotContain("Prior body", compiled.Content.GetRawText(), StringComparison.Ordinal);
        var included = Assert.Single(compiled.Manifest.Entries, entry =>
            entry.Included && entry.Kind == "artifact");
        Assert.Equal($"artifact:{active.ArtifactId}#/subject", included.Reference);
        Assert.True(compiled.Manifest.Provenance.ContainsKey(included.OutputPath!));
        Assert.Contains(compiled.Manifest.Entries, entry =>
            !entry.Included &&
            entry.Reference == $"artifact:{wrongProject.ArtifactId}" &&
            entry.Reason.Contains("tenant/project", StringComparison.Ordinal));
    }

    private static async Task<TaskDefinition> FindDefinitionAsync(string taskType) =>
        await new BuiltInTaskDefinitionRegistry().FindAsync(
            taskType,
            TestContext.Current.CancellationToken)
        ?? throw new InvalidOperationException($"Task definition '{taskType}' is missing.");

    private static MemoryRecord CreateMemory(
        string tenantId,
        string projectId,
        MemoryKind kind,
        string key,
        string value,
        DateTimeOffset createdAt)
    {
        var content = JsonSerializer.SerializeToElement(value);
        return new MemoryRecord(
            Guid.CreateVersion7(),
            tenantId,
            projectId,
            kind,
            key,
            1,
            content,
            $"hash-{key}",
            MemoryLifecycleStatus.Active,
            [new EvidenceReference("fixture", key)],
            [],
            createdAt,
            createdAt.AddHours(1));
    }

    private static ArtifactRecord CreateArtifact(
        string tenantId,
        string projectId,
        JsonElement content,
        DateTimeOffset expiresAt) => new(
        Guid.CreateVersion7(),
        tenantId,
        projectId,
        "email.draft",
        1,
        "email.draft",
        1,
        Guid.NewGuid().ToString("N"),
        Guid.NewGuid().ToString("N"),
        content,
        [new EvidenceReference("fixture", "artifact")],
        DateTimeOffset.UtcNow,
        expiresAt,
        true,
        Guid.NewGuid().ToString("N"));
}
