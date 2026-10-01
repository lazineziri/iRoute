using System.Globalization;
using System.Text;
using System.Text.Json;
using iRoute.Common;

namespace iRoute.Tests.Benchmarks;

internal sealed record BenchmarkObservation(bool Succeeded, bool FactsPassed, UsageSummary? Usage, int? HttpModelCalls,
    long ElapsedMilliseconds, string? Resolution, ContextManifest? Context, JsonElement? Output, string? ErrorCode);

internal sealed record ProductBenchmarkRow(string Model, string Case, string Order, BenchmarkObservation? Baseline,
    BenchmarkObservation Optimized);

internal sealed class ProductBenchmarkReport
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private static readonly string[] Exclusions = ["gpt-6-astra"];
    private readonly List<ProductBenchmarkRow> rows = [];
    private readonly string? methodology;
    public string DirectoryPath { get; }

    public ProductBenchmarkReport(string? methodology = null)
    {
        this.methodology = methodology;
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "iRoute.slnx"))) root = root.Parent;
        if (root is null) throw new InvalidOperationException("Run the product benchmark from a source checkout.");
        DirectoryPath = Path.Combine(root.FullName, "artifacts", "benchmarks", "run-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(DirectoryPath);
    }

    public async Task AddAsync(ProductBenchmarkRow row, CancellationToken token)
    {
        rows.Add(row);
        await File.AppendAllTextAsync(Path.Combine(DirectoryPath, "progress.jsonl"),
            JsonSerializer.Serialize(row, JsonSerializerOptions.Web) + "\n", token);
        await SaveAsync(token);
    }

    public async Task SaveAsync(CancellationToken token)
    {
        await File.WriteAllTextAsync(Path.Combine(DirectoryPath, "results.json"), JsonSerializer.Serialize(new
        {
            observedAtUtc = DateTimeOffset.UtcNow,
            methodology = methodology ?? "Single sample per model/scenario; synthetic fact assertions, not broad quality calibration. Same native adapter/schema/model/low reasoning; baseline bypasses iRoute optimization. Alternating order. Cache hits include cold creation elsewhere in this workload. Token totals include cached input and reasoning; neither is subtracted. Unknown failure usage is not zero. Subscription dollar cost remains unknown.",
            exclusions = Exclusions,
            rows
        }, JsonOptions), token);
        var text = new StringBuilder("# iRoute product benchmark\n\n");
        text.AppendLine(methodology ?? "Exploratory synthetic workload; one sample per pair, not a production savings claim. Both paths use the unmodified native adapter and low reasoning.");
        text.AppendLine();
        text.AppendLine("| Model | Case | Direct tokens | iRoute tokens | Token change | Direct / iRoute fact checks | iRoute calls | Direct / iRoute ms |");
        text.AppendLine("|---|---|---:|---:|---:|---|---:|---:|");
        foreach (var row in rows)
        {
            var baseline = Total(row.Baseline);
            var optimized = Total(row.Optimized);
            var change = baseline is > 0 && optimized is { } count ? ((count - baseline.Value) * 100m / baseline.Value).ToString("0.0", CultureInfo.InvariantCulture) + "%" : "n/a";
            text.AppendLine(CultureInfo.InvariantCulture, $"| {row.Model} | {row.Case} | {baseline?.ToString(CultureInfo.InvariantCulture) ?? "unknown"} | {optimized?.ToString(CultureInfo.InvariantCulture) ?? "unknown"} | {change} | {Pass(row.Baseline)} / {Pass(row.Optimized)} | {row.Optimized.HttpModelCalls?.ToString(CultureInfo.InvariantCulture) ?? "unknown"} | {row.Baseline?.ElapsedMilliseconds} / {row.Optimized.ElapsedMilliseconds} |");
        }
        text.AppendLine("\nCounts are provider-reported input + output tokens. Input includes provider-cached tokens; output includes reported reasoning. No dollar savings or model-quality measurements are inferred. Failed-call usage is unknown unless explicitly reported. Read results.json for outputs, errors, context decisions, and optional token breakdowns.");
        await File.WriteAllTextAsync(Path.Combine(DirectoryPath, "results.md"), text.ToString(), token);
    }

    private static int? Total(BenchmarkObservation? observation) => observation is { Succeeded: true, Usage: { } usage }
        ? usage.InputTokens + usage.OutputTokens : null;
    private static string Pass(BenchmarkObservation? observation) => observation is null ? "n/a" : observation.Succeeded && observation.FactsPassed ? "pass" : "FAIL";
}
