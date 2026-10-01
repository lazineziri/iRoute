using System.Text.Json;
using iRoute.Common;

namespace iRoute.Tests.Optimization;

internal sealed class OptimizationGateway : IModelGateway
{
    private static readonly string[] Highlights = ["No actions required."];
    public List<ModelGatewayRequest> Requests { get; } = [];

    public Task<ModelGatewayResult> ExecuteAsync(ModelGatewayRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Requests.Add(request);
        var output = request.Capability == "text.summarization"
            ? JsonSerializer.SerializeToElement(new { summary = "Friday at 10:00.", highlights = Highlights })
            : JsonSerializer.SerializeToElement(new { subject = "Review", body = "Please review on Friday at 10:00.", tone = "professional" });
        return Task.FromResult(new ModelGatewayResult(output,
            new UsageSummary(120, 30, ModelCalls: 1, CostKnown: false, CachedInputTokens: 12, ReasoningTokens: 4), 0.9m, [], "optimization-test"));
    }
}
