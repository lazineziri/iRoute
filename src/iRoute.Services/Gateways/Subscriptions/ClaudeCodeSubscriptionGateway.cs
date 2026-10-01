using System.Diagnostics;
using System.Text.Json;
using iRoute.Common;

namespace iRoute.Services;

public sealed class ClaudeCodeSubscriptionGateway(IProviderCliRunner cli, ModelGatewayDeploymentOptions route) : IModelGateway
{
    public string GatewayId => route.GatewayId;

    public async Task<ModelGatewayResult> ExecuteAsync(ModelGatewayRequest request, CancellationToken cancellationToken)
    {
        SubscriptionGatewaySupport.EnsureRequest(request, route);
        var executable = string.IsNullOrWhiteSpace(route.ExecutablePath) ? "claude" : route.ExecutablePath;
        var help = await cli.RunAsync(new ProviderCliInvocation(executable, ["--help"], "", DeadlineMilliseconds: 5000), cancellationToken);
        SubscriptionGatewaySupport.EnsureSuccess(help);
        foreach (var flag in RequiredFlags)
            if (!help.Output.Contains(flag, StringComparison.Ordinal))
                throw ProviderResponse.Invalid(request, "Upgrade Claude Code: required subscription safety flags are unavailable.");
        await EnsureSubscriptionLoginAsync(executable, cancellationToken);
        var started = Stopwatch.StartNew();
        var result = await cli.RunAsync(new ProviderCliInvocation(executable,
            ["--print", "--output-format", "json", "--model", route.Model!, "--tools", "",
             "--safe-mode", "--strict-mcp-config", "--mcp-config", "{\"mcpServers\":{}}", "--disallowedTools", "mcp__*",
             "--setting-sources", "", "--no-session-persistence", "--permission-mode", "dontAsk",
             "--json-schema", ProviderTaskFormat.Schema(request).ToJsonString(), "--max-turns", "1",
             "--system-prompt", ProviderTaskFormat.Instructions],
            ProviderTaskFormat.Prompt(request), DeadlineMilliseconds: request.DeadlineMilliseconds ?? 30_000), cancellationToken);
        SubscriptionGatewaySupport.EnsureSuccess(result);
        try
        {
            using var document = JsonDocument.Parse(result.Output);
            var response = document.RootElement;
            if (response.GetProperty("is_error").GetBoolean())
                throw ProviderResponse.Invalid(request, "Claude Code did not complete the requested task.");
            var usage = response.GetProperty("usage");
            var inputTokens = ProviderResponse.AnthropicInputTokens(usage, request, out var cached);
            var outputTokens = ProviderResponse.Tokens(usage, "output_tokens", request);
            var output = ProviderResponse.Result(response.GetProperty("structured_output").GetRawText(),
                inputTokens, outputTokens, request, route, started.ElapsedMilliseconds, cached,
                ProviderResponse.Detail(usage, "output_tokens_details", "thinking_tokens", outputTokens, request));
            return output with { Usage = output.Usage with { Cost = 0m, CostKnown = false } };
        }
        catch (Exception error) when (error is JsonException or KeyNotFoundException or InvalidOperationException or OverflowException)
        {
            throw ProviderResponse.Invalid(request, "Claude Code returned an unsupported structured result.");
        }
    }

    private async Task EnsureSubscriptionLoginAsync(string executable, CancellationToken cancellationToken)
    {
        var status = await cli.RunAsync(new ProviderCliInvocation(executable, ["auth", "status", "--json"], "",
            DeadlineMilliseconds: 5000), cancellationToken);
        var allowed = false;
        try
        {
            using var document = JsonDocument.Parse(status.Output);
            var login = document.RootElement;
            allowed = status.ExitCode == 0 && login.GetProperty("loggedIn").GetBoolean() &&
                login.GetProperty("authMethod").GetString() == "claude.ai" &&
                login.GetProperty("apiProvider").GetString() == "firstParty" &&
                login.GetProperty("subscriptionType").GetString() is "pro" or "max" or "team" or "enterprise" &&
                (!login.TryGetProperty("apiKeySource", out var source) || source.ValueKind == JsonValueKind.Null);
        }
        catch (Exception error) when (error is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            // Unknown status formats fail closed; never include account metadata in the error.
        }
        if (!allowed)
            throw new ModelGatewayException(ErrorCodes.ModelGatewayUnavailable,
                "Claude Code must report a recognized Claude subscription login, not Console/API credentials. Sign in through the current official CLI.",
                false, failureKind: ModelGatewayFailureKind.Authentication, failureClass: GatewayFailureClass.Permanent);
    }

    private static readonly string[] RequiredFlags =
        ["--safe-mode", "--tools", "--strict-mcp-config", "--json-schema", "--no-session-persistence"];
}
