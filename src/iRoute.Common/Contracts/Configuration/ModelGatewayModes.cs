namespace iRoute.Common;

public static class ModelGatewayModes
{
    public static bool IsSupported(string mode) => mode.Equals("Deterministic", StringComparison.OrdinalIgnoreCase) ||
        IsAdapter(mode);

    public static bool IsAdapter(string adapter) => adapter.Equals("Http", StringComparison.OrdinalIgnoreCase) ||
        adapter.Equals("OpenAI", StringComparison.OrdinalIgnoreCase) ||
        adapter.Equals("Anthropic", StringComparison.OrdinalIgnoreCase) ||
        IsSubscription(adapter);

    public static bool IsSubscription(string adapter) => adapter.Equals("OpenAIChatGPT", StringComparison.OrdinalIgnoreCase) ||
        adapter.Equals("ClaudeCode", StringComparison.OrdinalIgnoreCase);
}
