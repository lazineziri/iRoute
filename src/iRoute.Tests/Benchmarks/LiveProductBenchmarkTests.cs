using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Nodes;
using iRoute.Common;
using iRoute.Tests.Executions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace iRoute.Tests.Benchmarks;

public sealed class LiveProductBenchmarkTests
{
    private static readonly string[] Models = ["gpt-5.6-sol", "gpt-5.6-terra", "gpt-5.6-luna", "gpt-5.5"];
    public static bool Enabled => Environment.GetEnvironmentVariable("IROUTE_RUN_LIVE_BENCHMARK") == "1";

    [Fact(Skip = "Live model calls require IROUTE_RUN_LIVE_BENCHMARK=1.", SkipUnless = nameof(Enabled))]
    public async Task CompareDirectModelsWithTheCompleteIRouteExecutionPipeline()
    {
        var token = TestContext.Current.CancellationToken;
        var report = new ProductBenchmarkReport();
        var models = SelectedModels();
        var available = await ModelsAsync(token);
        foreach (var model in models)
        {
            Assert.Contains(model, available);
            using var http = new BenchmarkHttpHandler();
            await using var fixture = await ExecutionFixture.CreateAsync(model: model,
                configure: services => services.AddHttpClient("iroute-generic-gateway").ConfigurePrimaryHttpMessageHandler(() => http));
            var cases = ProductBenchmarkCases.Create();
            for (var index = 0; index < cases.Length; index++)
            {
                var item = cases[index];
                var request = ProductBenchmarkCases.Request(item, model + ":" + item.Id);
                BenchmarkObservation baseline;
                BenchmarkObservation optimized;
                if (index % 2 == 0)
                {
                    baseline = await ProductBenchmarkExecution.DirectAsync(fixture, http, item, request, token);
                    optimized = await ProductBenchmarkExecution.OptimizedAsync(fixture, http, item, request, token);
                }
                else
                {
                    optimized = await ProductBenchmarkExecution.OptimizedAsync(fixture, http, item, request, token);
                    baseline = await ProductBenchmarkExecution.DirectAsync(fixture, http, item, request, token);
                }
                await report.AddAsync(new(model, item.Id, index % 2 == 0 ? "direct-first" : "iroute-first", baseline, optimized), token);
                Assert.True(baseline.Succeeded && optimized.Succeeded, $"Inference failed; inspect {report.DirectoryPath}.");
                Assert.True(baseline.FactsPassed && optimized.FactsPassed, $"Factual regression; inspect {report.DirectoryPath}.");
            }
            var seed = cases[0];
            var repeat = ProductBenchmarkCases.Request(seed, model + ":" + seed.Id);
            var directRepeat = await ProductBenchmarkExecution.DirectAsync(fixture, http, seed, repeat, token);
            var warm = await ProductBenchmarkExecution.OptimizedAsync(fixture, http, seed, repeat, token);
            await report.AddAsync(new(model, "warm-new-request", "direct-first", directRepeat, warm), token);
            Assert.Equal(0, warm.HttpModelCalls);
            Assert.True(directRepeat.Succeeded && directRepeat.FactsPassed);
            Assert.True(warm.Succeeded && warm.FactsPassed);

            var changedInput = JsonNode.Parse(seed.Input.GetRawText())!.AsObject();
            changedInput["content"] = "The Atlas project meeting is Tuesday at 14:00. No external actions are authorized.";
            changedInput["facts"] = new JsonArray("The Atlas project meeting is Tuesday at 14:00.");
            var changed = seed with
            {
                Id = "changed-source",
                Input = JsonSerializer.SerializeToElement(changedInput),
                RequiredFacts = ["Atlas", "Tuesday", "14:00"],
                ForbiddenFacts = ["Friday", "10:00"]
            };
            var changedRequest = ProductBenchmarkCases.Request(changed, repeat.ProjectId!);
            var directChanged = await ProductBenchmarkExecution.DirectAsync(fixture, http, changed, changedRequest, token);
            var regenerated = await ProductBenchmarkExecution.OptimizedAsync(fixture, http, changed, changedRequest, token);
            await report.AddAsync(new(model, changed.Id, "direct-first", directChanged, regenerated), token);
            Assert.Equal(1, regenerated.HttpModelCalls);
            Assert.True(directChanged.Succeeded && regenerated.Succeeded && directChanged.FactsPassed && regenerated.FactsPassed,
                $"Changed-source factual regression; inspect {report.DirectoryPath}.");
        }
        await report.SaveAsync(token);
        Console.WriteLine($"Product benchmark results: {report.DirectoryPath}");
    }

    [Fact(Skip = "Live model calls require IROUTE_RUN_LIVE_BENCHMARK=1.", SkipUnless = nameof(Enabled))]
    public async Task SupersededSourcesCannotPoisonSubsequentNoModelLookups()
    {
        var token = TestContext.Current.CancellationToken;
        var models = SelectedModels();
        var available = await ModelsAsync(token);
        var report = new ProductBenchmarkReport();
        var seed = ProductBenchmarkCases.Create().Single(item => item.Id == "superseded-facts");
        var input = JsonNode.Parse(seed.Input.GetRawText())!.AsObject();
        input["facts"] = new JsonArray(input["facts"]!.AsArray().Reverse().Select(item => item!.DeepClone()).ToArray());
        var item = seed with { Id = "inactive-source-last", Input = JsonSerializer.SerializeToElement(input) };
        foreach (var model in models)
        {
            Assert.Contains(model, available);
            using var http = new BenchmarkHttpHandler();
            await using var fixture = await ExecutionFixture.CreateAsync(model: model,
                configure: services => services.AddHttpClient("iroute-generic-gateway").ConfigurePrimaryHttpMessageHandler(() => http));
            var request = ProductBenchmarkCases.Request(item, model + ":memory-followup");
            var generated = await ProductBenchmarkExecution.OptimizedAsync(fixture, http, item, request, token);
            await report.AddAsync(new(model, item.Id, "iroute-only", null, generated), token);
            Assert.True(generated.Succeeded && generated.FactsPassed, $"Factual regression; inspect {report.DirectoryPath}.");
            Assert.Equal(1, generated.HttpModelCalls);

            var lookup = seed with
            {
                Id = "no-model-memory-lookup",
                TaskType = "project.fact.get",
                Input = JsonSerializer.SerializeToElement(new { key = "meeting" })
            };
            var lookupRequest = ProductBenchmarkCases.Request(lookup, request.ProjectId!) with
            { Constraints = new TaskConstraints(MaxModelCalls: 0), PermissionScopes = ["project:read"] };
            var recalled = await ProductBenchmarkExecution.OptimizedAsync(fixture, http, lookup, lookupRequest, token);
            await report.AddAsync(new(model, lookup.Id, "iroute-only", null, recalled), token);
            Assert.True(recalled.Succeeded && recalled.FactsPassed, $"Stale project state; inspect {report.DirectoryPath}.");
            Assert.Equal(0, recalled.HttpModelCalls);
            Assert.Equal(nameof(ResolutionLevel.StructuredState), recalled.Resolution);
        }
        Console.WriteLine($"Project-memory follow-up results: {report.DirectoryPath}");
    }

    internal static string[] SelectedModels()
    {
        var selected = Environment.GetEnvironmentVariable("IROUTE_BENCHMARK_MODEL");
        if (selected is null) return Models;
        Assert.Contains(selected, Models);
        return [selected];
    }

    internal static async Task<HashSet<string>> ModelsAsync(CancellationToken token)
    {
        await using var fixture = await ExecutionFixture.CreateAsync(model: Models[0]);
        var access = await fixture.Services.GetRequiredService<IChatGPTAccessTokenProvider>().GetAsync(null, token);
        using var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false })
        { Timeout = TimeSpan.FromSeconds(30), MaxResponseContentBufferSize = 2 * 1024 * 1024 };
        using var message = new HttpRequestMessage(HttpMethod.Get, "https://api.openai.com/v1/models");
        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", access);
        using var response = await client.SendAsync(message, token);
        Assert.True(response.IsSuccessStatusCode, $"The live catalog returned HTTP {(int)response.StatusCode}; no API billing fallback was used.");
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(token));
        return document.RootElement.GetProperty("models").EnumerateArray()
            .Where(item => item.GetProperty("visibility").GetString() == "list")
            .Select(item => item.GetProperty("slug").GetString()!).ToHashSet(StringComparer.Ordinal);
    }
}
