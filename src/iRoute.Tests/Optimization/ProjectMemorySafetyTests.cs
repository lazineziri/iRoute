using System.Text.Json;
using iRoute.Common;
using iRoute.Data;
using iRoute.Tests.Executions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace iRoute.Tests.Optimization;

public sealed class ProjectMemorySafetyTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task NewestSourceVersionWinsRegardlessOfArrayOrder(bool oldFirst, bool inMemory)
    {
        await using var fixture = await ExecutionFixture.CreateAsync(new OptimizationGateway(),
            configure: services => { if (inMemory) services.AddSingleton<IMemoryStore>(new InMemoryMemoryStore()); });
        var current = new { key = "meeting", version = 2, content = "Friday 10:00" };
        var old = new { key = "meeting", version = 1, content = "Thursday 09:00" };
        var request = ExecutionFixture.Draft() with
        {
            ProjectId = "version-order",
            Input = JsonSerializer.SerializeToElement(new
            { objective = "Draft the meeting time.", facts = oldFirst ? new[] { old, current } : [current, old] })
        };
        await fixture.Executions.ExecuteAsync(request, Token);
        var memory = await fixture.Services.GetRequiredService<IMemoryStore>().GetActiveAsync(
            new MemoryLookup("tenant", request.ProjectId, MemoryKind.Fact, "meeting", DateTimeOffset.UtcNow), Token);
        Assert.NotNull(memory);
        Assert.Equal("Friday 10:00", memory.Value.GetProperty("content").GetString());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ADelayedOlderWriteCannotReplaceANewerStoredSource(bool inMemory)
    {
        await using var fixture = await ExecutionFixture.CreateAsync();
        var store = inMemory ? new InMemoryMemoryStore() : fixture.Services.GetRequiredService<IMemoryStore>();
        var now = DateTimeOffset.UtcNow;
        MemoryRecord Record(int version) => new(Guid.CreateVersion7(), "tenant", "delayed-source", MemoryKind.Fact,
            "meeting", 1, JsonSerializer.SerializeToElement(new { version, content = version == 2 ? "Friday" : "Thursday" }),
            version.ToString(System.Globalization.CultureInfo.InvariantCulture), MemoryLifecycleStatus.Active, [], [], now);
        var current = await store.UpsertAsync(Record(2), Token);
        var delayed = await store.UpsertAsync(Record(1), Token);
        Assert.False(delayed.Created);
        Assert.Equal(current.Record.MemoryId, delayed.Record.MemoryId);
        Assert.Equal("Friday", (await store.GetActiveAsync(new MemoryLookup("tenant", "delayed-source",
            MemoryKind.Fact, "meeting", now), Token))!.Value.GetProperty("content").GetString());
    }

    [Theory]
    [InlineData("isActive", "false")]
    [InlineData("status", "\"Expired\"")]
    [InlineData("lifecycleStatus", "\"Invalidated\"")]
    [InlineData("lifecycleStatus", "\"Superseded\"")]
    [InlineData("supersededBy", "\"newer-source\"")]
    [InlineData("expiresAt", "\"2000-01-01T00:00:00Z\"")]
    public async Task AnExcludedSourceCannotOverwriteCurrentMemory(string property, string rawValue)
    {
        await using var fixture = await ExecutionFixture.CreateAsync(new OptimizationGateway());
        var old = new Dictionary<string, object>
        {
            ["key"] = "meeting",
            ["content"] = "Thursday 09:00",
            [property] = JsonSerializer.Deserialize<JsonElement>(rawValue)
        };
        var request = ExecutionFixture.Draft() with
        {
            ProjectId = "memory-safety",
            Input = JsonSerializer.SerializeToElement(new
            {
                objective = "Draft the current meeting time.",
                facts = new object[] { new { key = "meeting", content = "Friday 10:00" }, old }
            })
        };
        var result = await fixture.Executions.ExecuteAsync(request, Token);
        Assert.Equal(ExecutionStatus.Succeeded, result.Status);
        var memory = await fixture.Services.GetRequiredService<IMemoryStore>().GetActiveAsync(
            new MemoryLookup("tenant", request.ProjectId, MemoryKind.Fact, "meeting", DateTimeOffset.UtcNow), Token);
        Assert.NotNull(memory);
        Assert.Equal("Friday 10:00", memory.Value.GetProperty("content").GetString());

        var lookup = await fixture.Executions.ExecuteAsync(new TaskRequest("project.fact.get",
            JsonSerializer.SerializeToElement(new { key = "meeting" }), ProjectId: request.ProjectId,
            IdempotencyKey: "lookup", TenantId: "tenant", ActorId: "requester",
            PermissionScopes: ["project:read"], Constraints: new TaskConstraints(MaxModelCalls: 0)), Token);
        Assert.Equal(ExecutionStatus.Succeeded, lookup.Status);
        Assert.Equal(ResolutionLevel.StructuredState, lookup.Outcome!.ResolutionLevel);
        Assert.Equal(0, lookup.Outcome.Usage.ModelCalls);
        Assert.Equal("Friday 10:00", lookup.Outcome.Output.GetProperty("value").GetProperty("content").GetString());
    }

    [Fact]
    public async Task MaterializedMemoryRetainsItsSourceExpiry()
    {
        await using var fixture = await ExecutionFixture.CreateAsync(new OptimizationGateway());
        var expiresAt = DateTimeOffset.FromUnixTimeMilliseconds(DateTimeOffset.UtcNow.AddMinutes(5).ToUnixTimeMilliseconds());
        var request = ExecutionFixture.Draft() with
        {
            ProjectId = "expiring-memory",
            Input = JsonSerializer.SerializeToElement(new
            {
                objective = "Draft a meeting note.",
                facts = new[] { new { key = "meeting", content = "Friday 10:00", expiresAt } }
            })
        };
        await fixture.Executions.ExecuteAsync(request, Token);
        var store = fixture.Services.GetRequiredService<IMemoryStore>();
        var memory = await store.GetActiveAsync(new MemoryLookup("tenant", request.ProjectId, MemoryKind.Fact,
            "meeting", DateTimeOffset.UtcNow), Token);
        Assert.NotNull(memory);
        Assert.Equal(expiresAt, memory.ExpiresAt);
        Assert.Null(await store.GetActiveAsync(new MemoryLookup("tenant", request.ProjectId, MemoryKind.Fact,
            "meeting", expiresAt.AddSeconds(1)), Token));
    }
}
