using System.Text.Encodings.Web;
using System.Text.Json;
using iRoute.Common;
using iRoute.Tests.Executions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace iRoute.Tests.Optimization;

public sealed class ContextOptimizationTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private static readonly string[] RepeatedFacts = ["Friday at 10:00.", "Friday at 10:00.", "Friday at 10:00."];
    private static readonly string[] MeetingFacts = ["Meeting confirmed."];
    private static readonly JsonSerializerOptions UnicodeJson = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    [Fact]
    public async Task DelayedRequestCannotReplaceNewerStoredMemoryInModelContext()
    {
        var gateway = new OptimizationGateway();
        await using var fixture = await ExecutionFixture.CreateAsync(gateway);
        var memory = new MemoryRecord(Guid.CreateVersion7(), "tenant", "project", MemoryKind.Fact, "meeting", 1,
            JsonSerializer.SerializeToElement(new { key = "meeting", version = 12, content = "Thursday 15:30" }),
            "newer", MemoryLifecycleStatus.Active, [], [], DateTimeOffset.UtcNow);
        await fixture.Services.GetRequiredService<IMemoryStore>().UpsertAsync(memory, Token);
        var result = await fixture.Executions.ExecuteAsync(ExecutionFixture.Draft() with
        {
            ProjectId = "project",
            Input = JsonSerializer.SerializeToElement(new
            {
                objective = "Draft the current meeting time.",
                facts = new[] { new { id = "meeting", version = 2, content = "Monday 09:00" } }
            })
        }, Token);
        Assert.Equal(ExecutionStatus.Succeeded, result.Status);
        var context = Assert.Single(gateway.Requests).Context.GetRawText();
        Assert.Contains("Thursday", context, StringComparison.Ordinal);
        Assert.DoesNotContain("Monday", context, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("承認コード", "承認コード: AX-417")]
    [InlineData("الاعتماد", "الاعتماد: AX-417")]
    [InlineData("审批", "审批: AX-417")]
    public async Task UnicodeRelevanceKeepsAnOlderMatchingHistoryItem(string keyword, string fact)
    {
        await using var fixture = await ExecutionFixture.CreateAsync();
        var request = ExecutionFixture.Draft() with
        {
            Input = JsonSerializer.SerializeToElement(new
            {
                objective = keyword,
                projectHistory = new[] { fact }.Concat(Enumerable.Range(0, 10).Select(index => $"Unrelated archived note {index}.")).ToArray()
            }, UnicodeJson)
        };
        var definition = (await fixture.Services.GetRequiredService<ITaskDefinitionRegistry>().FindAsync("email.draft", Token))!;
        var compiled = await fixture.Services.GetRequiredService<IContextCompiler>().CompileAsync(request, definition, Token);
        Assert.Contains(compiled.Content.GetProperty("projectHistory").EnumerateArray(), value => value.GetString() == fact);
    }

    [Fact]
    public async Task DuplicateContextIsNotSentTwiceOrLeftInProjectedInput()
    {
        var gateway = new OptimizationGateway();
        await using var fixture = await ExecutionFixture.CreateAsync(gateway);
        var request = ExecutionFixture.Draft() with
        {
            Input = JsonSerializer.SerializeToElement(new
            {
                objective = "Draft a brief meeting note.",
                facts = RepeatedFacts
            })
        };
        var result = await fixture.Executions.ExecuteAsync(request, Token);
        Assert.Equal(ExecutionStatus.Succeeded, result.Status);
        var sent = Assert.Single(gateway.Requests);
        Assert.False(sent.Input.TryGetProperty("facts", out _));
        Assert.Single(sent.Context.GetProperty("facts").EnumerateArray());
        Assert.Equal(2, result.Outcome!.Context!.Entries.Count(item => !item.Included && item.Reason.Contains("duplicate", StringComparison.Ordinal)));
        Assert.False(result.Outcome.Routing!.PlannerInvoked);
        Assert.Equal(0, result.Outcome.Routing.PlanningCalls);
    }

    [Fact]
    public async Task ContextSelectionKeepsCurrentFactsAndExcludesInactiveSources()
    {
        await using var fixture = await ExecutionFixture.CreateAsync();
        var request = ExecutionFixture.Draft() with
        {
            Input = JsonSerializer.SerializeToElement(new
            {
                objective = "State the current meeting time.",
                facts = new[] { new { key = "meeting", version = 2, content = "Friday 10:00", isActive = true },
                new { key = "meeting", version = 1, content = "Thursday 09:00", isActive = false } },
                projectHistory = Enumerable.Range(0, 12).Select(index => $"Historical note {index}.").ToArray()
            })
        };
        var definition = (await fixture.Services.GetRequiredService<ITaskDefinitionRegistry>().FindAsync("email.draft", Token))!;
        var compiled = await fixture.Services.GetRequiredService<IContextCompiler>().CompileAsync(request, definition, Token);
        Assert.Contains("Friday", compiled.Content.GetRawText(), StringComparison.Ordinal);
        Assert.DoesNotContain("Thursday", compiled.Content.GetRawText(), StringComparison.Ordinal);
        Assert.Equal(3, compiled.Content.GetProperty("projectHistory").GetArrayLength());
        Assert.True(compiled.Manifest.Truncated);
        Assert.False(compiled.Manifest.FullHistoryIncluded);
        Assert.True(compiled.Manifest.EstimatedTokens <= compiled.Manifest.BudgetTokens);
    }

    [Theory]
    [InlineData("Shqip: takimi është të premten në 10:00.")]
    [InlineData("日本語: 会議は金曜日10:00です。")]
    [InlineData("Arabic: الاجتماع يوم الجمعة الساعة 10:00.")]
    public async Task MultilingualTaskContentIsPreserved(string text)
    {
        var gateway = new OptimizationGateway();
        await using var fixture = await ExecutionFixture.CreateAsync(gateway);
        await fixture.Executions.ExecuteAsync(ExecutionFixture.Draft() with { Input = JsonSerializer.SerializeToElement(new { objective = text, facts = MeetingFacts }) }, Token);
        Assert.Equal(text, Assert.Single(gateway.Requests).Input.GetProperty("objective").GetString());
    }
}
