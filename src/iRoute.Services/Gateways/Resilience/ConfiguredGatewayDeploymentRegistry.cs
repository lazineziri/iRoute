using iRoute.Common;
using Microsoft.Extensions.Options;

namespace iRoute.Services;

public sealed class ConfiguredGatewayDeploymentRegistry : IGatewayDeploymentRegistry
{
    private readonly IReadOnlyList<GatewayDeployment> _deployments;

    public ConfiguredGatewayDeploymentRegistry(IOptions<ModelGatewayOptions> configuredOptions)
    {
        var options = configuredOptions.Value;
        options.Resilience.EnsureValid();
        _deployments = EffectiveOptions(options).Select(ToDeployment).ToArray();
        var duplicateRoute = _deployments
            .GroupBy(item => item.RouteId, StringComparer.Ordinal)
            .FirstOrDefault(group => group.Count() > 1);
        var duplicateDeployment = _deployments
            .GroupBy(item => item.DeploymentId, StringComparer.Ordinal)
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicateRoute is not null || duplicateDeployment is not null)
        {
            throw new InvalidOperationException(
                "ModelGateway deployment route IDs and deployment IDs must be unique.");
        }
    }

    public Task<IReadOnlyList<GatewayDeployment>> ListAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(_deployments);
    }

    internal static IReadOnlyList<ModelGatewayDeploymentOptions> EffectiveOptions(ModelGatewayOptions options) =>
        options.Deployments.Count > 0
            ? options.Deployments
            :
            [
                new ModelGatewayDeploymentOptions
                {
                    RouteId = "legacy",
                    GatewayId = options.GatewayId,
                    DeploymentId = options.GatewayId,
                    Provider = "generic",
                    Region = "unspecified",
                    Residency = "unspecified",
                    ModelVersion = "unspecified",
                    Capabilities = ["*"],
                    ProfileIds = ["*"],
                    ExpectedQuality = 1m,
                    EstimatedCost = 0m,
                    ExpectedLatencyMilliseconds = 0,
                    Priority = 100,
                    Enabled = true,
                    Transport = options.Transport,
                    BaseUrl = options.BaseUrl,
                    ApiKey = options.ApiKey,
                    ExecutePath = options.ExecutePath,
                    StreamPath = options.StreamPath,
                    HealthPath = options.HealthPath
                }
            ];

    private static GatewayDeployment ToDeployment(ModelGatewayDeploymentOptions route)
    {
        var invalidFields = InvalidFields(route);
        if (invalidFields.Count > 0)
        {
            throw new InvalidOperationException(
                $"Generic gateway route '{route.RouteId}' has invalid configuration fields: " +
                string.Join(", ", invalidFields) + ".");
        }

        return new GatewayDeployment(
            route.RouteId,
            route.GatewayId,
            route.Provider,
            route.DeploymentId,
            route.Region,
            route.Residency,
            route.ModelVersion,
            route.Capabilities.ToArray(),
            route.ProfileIds.ToArray(),
            route.ExpectedQuality,
            route.EstimatedCost,
            route.ExpectedLatencyMilliseconds,
            route.Priority,
            route.Enabled);
    }

    private static List<string> InvalidFields(ModelGatewayDeploymentOptions route)
    {
        var invalid = new List<string>();
        if (string.IsNullOrWhiteSpace(route.RouteId)) invalid.Add(nameof(route.RouteId));
        if (string.IsNullOrWhiteSpace(route.GatewayId)) invalid.Add(nameof(route.GatewayId));
        if (string.IsNullOrWhiteSpace(route.DeploymentId)) invalid.Add(nameof(route.DeploymentId));
        if (string.IsNullOrWhiteSpace(route.Provider)) invalid.Add(nameof(route.Provider));
        if (string.IsNullOrWhiteSpace(route.Region)) invalid.Add(nameof(route.Region));
        if (string.IsNullOrWhiteSpace(route.Residency)) invalid.Add(nameof(route.Residency));
        if (string.IsNullOrWhiteSpace(route.ModelVersion)) invalid.Add(nameof(route.ModelVersion));
        if (route.Capabilities.Count == 0 ||
            route.Capabilities.Any(string.IsNullOrWhiteSpace) ||
            route.Capabilities.Distinct(StringComparer.Ordinal).Count() != route.Capabilities.Count)
        {
            invalid.Add(nameof(route.Capabilities));
        }
        if (route.ProfileIds.Count == 0 ||
            route.ProfileIds.Any(string.IsNullOrWhiteSpace) ||
            route.ProfileIds.Distinct(StringComparer.Ordinal).Count() != route.ProfileIds.Count)
        {
            invalid.Add(nameof(route.ProfileIds));
        }
        if (route.ExpectedQuality is < 0m or > 1m) invalid.Add(nameof(route.ExpectedQuality));
        if (route.EstimatedCost < 0m) invalid.Add(nameof(route.EstimatedCost));
        if (route.ExpectedLatencyMilliseconds < 0) invalid.Add(nameof(route.ExpectedLatencyMilliseconds));
        if (route.Priority < 0) invalid.Add(nameof(route.Priority));
        if (route.Enabled && !IsHttpBaseUrl(route.BaseUrl)) invalid.Add(nameof(route.BaseUrl));
        return invalid;
    }

    private static bool IsHttpBaseUrl(string? value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
        (string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) ||
         string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase));
}
