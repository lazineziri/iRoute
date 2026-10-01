using iRoute.Common;
using Xunit;

namespace iRoute.Tests.Contracts;

public sealed class CredentialRedactionTests
{
    [Fact]
    public void GatewayConfigurationDoesNotExposeCredentialsThroughRecordFormatting()
    {
        var deployment = new ModelGatewayDeploymentOptions
        {
            ApiKey = "example-api-credential",
            ChatGPTAccessToken = "example-plan-credential",
            BaseUrl = "https://example.invalid/?token=example-query-credential"
        };
        var options = new ModelGatewayOptions
        {
            ApiKey = deployment.ApiKey,
            ChatGPTAccessToken = deployment.ChatGPTAccessToken,
            BaseUrl = deployment.BaseUrl,
            Deployments = [deployment]
        };

        Assert.Equal("ModelGatewayOptions { configuration = [redacted] }", options.ToString());
        Assert.Equal("ModelGatewayDeploymentOptions { configuration = [redacted] }", deployment.ToString());
        Assert.DoesNotContain("example-", $"{options} {deployment}", StringComparison.Ordinal);
    }
}
