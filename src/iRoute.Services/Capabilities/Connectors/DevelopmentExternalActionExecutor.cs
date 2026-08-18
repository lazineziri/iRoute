using System.Text.Json;
using iRoute.Common;

namespace iRoute.Services;

public sealed class DevelopmentExternalActionExecutor : IExternalActionExecutor
{
    public Task<ExternalActionResult> ExecuteAsync(
        ExternalActionRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!string.Equals(request.Capability, "email.send", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"The development external-action executor does not implement '{request.Capability}'.");
        }

        var output = JsonSerializer.SerializeToElement(new
        {
            receiptId = $"sim-{request.IdempotencyKey[..24]}",
            status = "simulated",
            capability = request.Capability
        });
        return Task.FromResult(new ExternalActionResult(
            output,
            [new EvidenceReference("external-action", $"receipt:{request.IdempotencyKey}")]));
    }
}
