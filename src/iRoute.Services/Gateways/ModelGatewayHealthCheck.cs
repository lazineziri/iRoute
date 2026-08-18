using iRoute.Common;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace iRoute.Services;

public sealed class ModelGatewayHealthCheck(IModelGateway gateway) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        var health = await gateway.CheckHealthAsync(cancellationToken);
        var data = new Dictionary<string, object>(StringComparer.Ordinal)
        {
            ["gatewayId"] = health.GatewayId,
            ["latencyMilliseconds"] = health.LatencyMilliseconds,
            ["checkedAt"] = health.CheckedAt
        };
        return health.Status switch
        {
            ModelGatewayHealthStatus.Healthy => HealthCheckResult.Healthy(health.Message, data),
            ModelGatewayHealthStatus.Degraded => HealthCheckResult.Degraded(health.Message, data: data),
            _ => HealthCheckResult.Unhealthy(health.Message, data: data)
        };
    }
}
