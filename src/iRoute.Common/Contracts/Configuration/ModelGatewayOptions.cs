namespace iRoute.Common;

public sealed record ModelGatewayOptions
{
    public string Mode { get; init; } = "Deterministic";
    public string GatewayId { get; init; } = "external";
    public ModelGatewayTransport Transport { get; init; } = ModelGatewayTransport.Buffered;
    public string? BaseUrl { get; init; }
    public string? ApiKey { get; init; }
    public string? ChatGPTAccessToken { get; init; }
    public Guid? ChatGPTAccountId { get; init; }
    public string? Model { get; init; }
    public string? ReasoningEffort { get; init; }
    public decimal ExpectedQuality { get; init; } = 0.9m;
    public string? ExecutablePath { get; init; }
    public string SubscriptionTenantId { get; init; } = "local";
    public decimal? InputCostPerMillionTokens { get; init; }
    public decimal? OutputCostPerMillionTokens { get; init; }
    public string ExecutePath { get; init; } = "v1/execute";
    public string StreamPath { get; init; } = "v1/stream";
    public string HealthPath { get; init; } = "health";
    public List<ModelGatewayDeploymentOptions> Deployments { get; init; } = [];
    public GatewayResilienceOptions Resilience { get; init; } = new();

    public override string ToString() => "ModelGatewayOptions { configuration = [redacted] }";
}

public sealed record ModelGatewayDeploymentOptions
{
    public string Adapter { get; init; } = "Http";
    public string RouteId { get; init; } = string.Empty;
    public string GatewayId { get; init; } = string.Empty;
    public string Provider { get; init; } = "generic";
    public string DeploymentId { get; init; } = string.Empty;
    public string Region { get; init; } = "unspecified";
    public string Residency { get; init; } = "unspecified";
    public string ModelVersion { get; init; } = "unspecified";
    public List<string> Capabilities { get; init; } = [];
    public List<string> ProfileIds { get; init; } = [];
    public decimal ExpectedQuality { get; init; } = 1m;
    public decimal EstimatedCost { get; init; }
    public int ExpectedLatencyMilliseconds { get; init; }
    public int Priority { get; init; } = 100;
    public bool Enabled { get; init; } = true;
    public ModelGatewayTransport Transport { get; init; } = ModelGatewayTransport.Buffered;
    public string? BaseUrl { get; init; }
    public string? ApiKey { get; init; }
    public string? ChatGPTAccessToken { get; init; }
    public Guid? ChatGPTAccountId { get; init; }
    public string? Model { get; init; }
    public string? ReasoningEffort { get; init; }
    public string? ExecutablePath { get; init; }
    public string SubscriptionTenantId { get; init; } = "local";
    public decimal? InputCostPerMillionTokens { get; init; }
    public decimal? OutputCostPerMillionTokens { get; init; }
    public string ExecutePath { get; init; } = "v1/execute";
    public string StreamPath { get; init; } = "v1/stream";
    public string HealthPath { get; init; } = "health";

    public override string ToString() => "ModelGatewayDeploymentOptions { configuration = [redacted] }";
}

public sealed record GatewayResilienceOptions
{
    public bool Enabled { get; init; } = true;
    public int MaximumAttempts { get; init; } = 3;
    public GatewayCircuitPolicy Circuit { get; init; } = new();

    public void EnsureValid()
    {
        if (MaximumAttempts is < 1 or > 10)
        {
            throw new InvalidOperationException("Gateway resilience attempts must be between 1 and 10.");
        }

        Circuit.EnsureValid();
    }
}
