using System.Text.Json;
using System.Text.Json.Nodes;
using iRoute.Common;

namespace iRoute.Tests.Benchmarks;

internal sealed record ProductBenchmarkCase(string Id, string TaskType, JsonElement Input, string[] RequiredFacts, string[] ForbiddenFacts);

internal static class ProductBenchmarkCases
{
    private static readonly string[] Meeting = ["The Atlas project meeting is Friday at 10:00. No external actions are authorized."];
    private static readonly string[] Required = ["Atlas", "Friday", "10:00"];
    private static readonly string[] None = [];
    private static readonly string[] ApprovalHistory = ["Confirmed approval code: AX-417."];

    public static ProductBenchmarkCase[] Create()
    {
        var draft = JsonSerializer.SerializeToElement(new
        {
            objective = "Draft a professional meeting reminder in at most 45 words. Include the project, day and time. Do not invent details.",
            facts = Meeting
        });
        var summary = JsonSerializer.SerializeToElement(new
        {
            objective = "Return an English summary in at most 35 words and at most two short highlights. Include project, day and time.",
            content = "The Atlas project meeting is Friday at 10:00. No external actions are authorized.",
            facts = Meeting
        });
        var history = JsonNode.Parse(draft.GetRawText())!.AsObject();
        history["projectHistory"] = JsonSerializer.SerializeToNode(Enumerable.Range(1, 48)
            .Select(index => $"Archived note {index}: a completed warehouse inventory discussion concerned old delivery records, stock counts, packaging colors and filing procedures. It is unrelated to the current project meeting.").ToArray());
        var duplicates = JsonNode.Parse(draft.GetRawText())!.AsObject();
        duplicates["facts"] = JsonSerializer.SerializeToNode(Enumerable.Repeat(Meeting[0], 16).ToArray());
        var superseded = JsonNode.Parse(draft.GetRawText())!.AsObject();
        superseded["facts"] = JsonSerializer.SerializeToNode(new[]
        {
            new { key = "meeting", version = 1, isActive = false, content = "The Atlas meeting was Monday at 09:00, but this fact is superseded." },
            new { key = "meeting", version = 2, isActive = true, content = Meeting[0] }
        });
        var multilingual = JsonNode.Parse(summary.GetRawText())!.AsObject();
        multilingual["content"] = "Takimi i projektit Atlas është të premten në orën 10:00. Nuk autorizohen veprime të jashtme.";
        var critical = JsonNode.Parse(summary.GetRawText())!.AsObject();
        critical["objective"] = "Return a brief English summary with the confirmed approval code and meeting project, day and time. Do not invent a code.";
        critical["projectHistory"] = JsonSerializer.SerializeToNode(ApprovalHistory
            .Concat(Enumerable.Range(1, 20).Select(index => $"Archived shipping note {index}: packaging and old inventory discussion, unrelated to the current request.")).ToArray());
        return
        [
            new("summary-short", "document.summarize", summary, Required, None),
            new("draft-short", "email.draft", draft, Required, None),
            new("long-history", "email.draft", JsonSerializer.SerializeToElement(history), Required, None),
            new("duplicate-context", "email.draft", JsonSerializer.SerializeToElement(duplicates), Required, None),
            new("superseded-facts", "email.draft", JsonSerializer.SerializeToElement(superseded), Required, ["Monday", "09:00"]),
            new("multilingual", "document.summarize", JsonSerializer.SerializeToElement(multilingual), Required, None),
            new("critical-history", "document.summarize", JsonSerializer.SerializeToElement(critical), [.. Required, "AX-417"], None)
        ];
    }

    public static TaskRequest Request(ProductBenchmarkCase item, string project) => new(item.TaskType, item.Input, project,
        Guid.NewGuid().ToString("N"), new TaskConstraints(MaxInputTokens: 12000, MaxOutputTokens: 1024,
            DeadlineMilliseconds: 30000, MaxModelCalls: 1, MaxToolCalls: 0), TenantId: "local", ActorId: "benchmark");

    public static bool Passes(ProductBenchmarkCase item, JsonElement output)
    {
        var text = output.GetRawText();
        return item.RequiredFacts.All(fact => text.Contains(fact, StringComparison.OrdinalIgnoreCase)) &&
            item.ForbiddenFacts.All(fact => !text.Contains(fact, StringComparison.OrdinalIgnoreCase));
    }
}
