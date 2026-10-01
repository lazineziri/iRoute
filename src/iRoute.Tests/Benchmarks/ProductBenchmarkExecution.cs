using System.Diagnostics;
using System.Text.Json;
using iRoute.Common;
using iRoute.Tests.Executions;
using Microsoft.Extensions.DependencyInjection;

namespace iRoute.Tests.Benchmarks;

internal static class ProductBenchmarkExecution
{
    private static readonly JsonElement Empty = JsonSerializer.SerializeToElement(new { });

    public static async Task<BenchmarkObservation> DirectAsync(ExecutionFixture fixture, BenchmarkHttpHandler http,
        ProductBenchmarkCase item, TaskRequest request, CancellationToken token)
    {
        var calls = http.ModelCalls;
        var watch = Stopwatch.StartNew();
        try
        {
            var result = await fixture.Services.GetRequiredService<IModelGateway>().ExecuteAsync(new ModelGatewayRequest(
                item.TaskType == "email.draft" ? "text.generation" : "text.summarization", request.Input, Empty, 1024,
                DeadlineMilliseconds: 30000, TenantId: "local"), token);
            return new(true, ProductBenchmarkCases.Passes(item, result.Output), result.Usage,
                http.ModelCalls - calls, watch.ElapsedMilliseconds, "DirectModel", null, result.Output, null);
        }
        catch (ModelGatewayException error)
        {
            return new(false, false, null, http.ModelCalls - calls, watch.ElapsedMilliseconds, null, null, null, error.Code);
        }
    }

    public static async Task<BenchmarkObservation> OptimizedAsync(ExecutionFixture fixture, BenchmarkHttpHandler http,
        ProductBenchmarkCase item, TaskRequest request, CancellationToken token)
    {
        var calls = http.ModelCalls;
        var watch = Stopwatch.StartNew();
        var result = await fixture.Executions.ExecuteAsync(request, token);
        return new(result.Status == ExecutionStatus.Succeeded,
            result.Outcome is { } outcome && ProductBenchmarkCases.Passes(item, outcome.Output), result.Outcome?.Usage,
            http.ModelCalls - calls, watch.ElapsedMilliseconds, result.Outcome?.ResolutionLevel.ToString(),
            result.Outcome?.Context, result.Outcome?.Output, result.Error?.Code);
    }
}
