using System.Diagnostics;
using iRoute.Common;
using iRoute.Services;
using iRoute.Tests.Executions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace iRoute.Tests.Benchmarks;

public sealed class LiveHardeningTests
{
    public static bool ChatGPTEnabled => Environment.GetEnvironmentVariable("IROUTE_RUN_LIVE_HARDENING") == "1";
    public static bool ClaudeEnabled => Environment.GetEnvironmentVariable("IROUTE_RUN_LIVE_CLAUDE") == "1";
    private const string Methodology = "Two independent cold repetitions per synthetic scenario/model through the complete iRoute pipeline. Field-specific deterministic factual/status/negative-action rubrics with offline negative controls, not human semantic grading or quality calibration. No direct baseline: this is correctness validation, not a savings measurement. No Astra. Subscription dollar cost is unknown; Claude HTTP call count is unknown (CLI invocation/usage only).";

    [Fact(Skip = "Requires IROUTE_RUN_LIVE_HARDENING=1 and native iRoute ChatGPT sign-in.", SkipUnless = nameof(ChatGPTEnabled))]
    public async Task RepeatedColdOutputsPreserveCurrentFactsPaymentStatusAndInstructionBoundaries()
    {
        var token = TestContext.Current.CancellationToken;
        var available = await LiveProductBenchmarkTests.ModelsAsync(token);
        var report = new ProductBenchmarkReport(Methodology);
        foreach (var model in LiveProductBenchmarkTests.SelectedModels())
        {
            Assert.Contains(model, available);
            using var http = new BenchmarkHttpHandler();
            await using var fixture = await ExecutionFixture.CreateAsync(model: model,
                configure: services => services.AddHttpClient("iroute-generic-gateway").ConfigurePrimaryHttpMessageHandler(() => http));
            await RunCasesAsync(fixture, report, model, () => http.ModelCalls, token);
        }
        Console.WriteLine($"Repeated ChatGPT correctness results: {report.DirectoryPath}");
    }

    [Fact(Skip = "Requires IROUTE_RUN_LIVE_CLAUDE=1 and an official Claude Code subscription login.", SkipUnless = nameof(ClaudeEnabled))]
    public async Task OfficialClaudeSubscriptionCompletesTheNativeIRoutePipelineWithoutTools()
    {
        var route = new ModelGatewayDeploymentOptions
        {
            Adapter = "ClaudeCode",
            Model = "sonnet",
            GatewayId = "claude-native",
            SubscriptionTenantId = "local",
            ExpectedQuality = 0.95m
        };
        var gateway = new ClaudeCodeSubscriptionGateway(new ProviderCliRunner(TimeProvider.System), route);
        await using var fixture = await ExecutionFixture.CreateAsync(gateway);
        var report = new ProductBenchmarkReport(Methodology);
        await RunCasesAsync(fixture, report, "claude-sonnet-alias", null, TestContext.Current.CancellationToken);
        Console.WriteLine($"Native Claude correctness results: {report.DirectoryPath}");
    }

    private static async Task RunCasesAsync(ExecutionFixture fixture, ProductBenchmarkReport report, string model,
        Func<int>? callCount, CancellationToken token)
    {
        var failures = new List<string>();
        for (var repeat = 1; repeat <= 2; repeat++)
        {
            foreach (var item in OutputRubric.Cases())
            {
                var request = ProductBenchmarkCases.Request(item, $"{model}:{item.Id}:repeat-{repeat}") with
                { Constraints = new TaskConstraints(MaxOutputTokens: 1024, DeadlineMilliseconds: 90000, MaxModelCalls: 1, MaxToolCalls: 0) };
                var calls = callCount?.Invoke();
                var watch = Stopwatch.StartNew();
                var result = await fixture.Executions.ExecuteAsync(request, token);
                var passed = result.Outcome is { } outcome && OutputRubric.Passes(item.Id, outcome.Output);
                var observation = new BenchmarkObservation(result.Status == ExecutionStatus.Succeeded, passed, result.Outcome?.Usage,
                    calls is { } before ? callCount!() - before : null, watch.ElapsedMilliseconds,
                    result.Outcome?.ResolutionLevel.ToString(), result.Outcome?.Context, result.Outcome?.Output, result.Error?.Code);
                await report.AddAsync(new(model, item.Id + $":repeat-{repeat}", "iroute-only-cold", null, observation), token);
                Assert.True(observation.Succeeded, $"Native inference failed ({result.Error?.Code}); inspect {report.DirectoryPath} before spending more quota.");
                if (!observation.Succeeded || !passed) failures.Add(item.Id + $":repeat-{repeat} ({result.Error?.Code ?? "rubric"})");
            }
        }
        Assert.True(failures.Count == 0, $"Correctness failures: {string.Join(", ", failures)}; inspect {report.DirectoryPath}.");
    }
}
