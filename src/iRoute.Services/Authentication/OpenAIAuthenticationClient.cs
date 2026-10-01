using System.Net;
using System.Text.Json;
using iRoute.Common;

namespace iRoute.Services;

public sealed class OpenAIAuthenticationClient(HttpClient client, TimeProvider clock)
{
    public const string Issuer = "https://auth.openai.com";
    public const string Resource = "https://api.openai.com/v1";
    private const string TokenEndpoint = Issuer + "/api/accounts/oauth/token";
    private const string DiscoveryEndpoint = Issuer + "/.well-known/openid-configuration";
    private static readonly HashSet<string> SafeErrorCodes = new(StringComparer.Ordinal)
    {
        "invalid_grant", "invalid_client", "invalid_refresh_token", "token_expired",
        "refresh_token_expired", "refresh_token_invalidated", "refresh_token_reused"
    };

    public Task<ChatGPTTokens> ExchangeAsync(string clientId, string code, ChatGPTAuthorizationAttempt attempt,
        CancellationToken cancellationToken) => TokensAsync(new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["client_id"] = clientId,
            ["code"] = code,
            ["code_verifier"] = attempt.Verifier,
            ["redirect_uri"] = attempt.RedirectUri,
            ["resource"] = Resource
        }, null, cancellationToken);

    public Task<ChatGPTTokens> RefreshAsync(ChatGPTAccount account, CancellationToken cancellationToken) =>
        TokensAsync(new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["client_id"] = account.ClientId,
            ["refresh_token"] = account.Tokens!.RefreshToken,
            ["resource"] = Resource
        }, account.Tokens, cancellationToken);

    public async Task<JsonElement> ReadDiscoveryAsync(CancellationToken cancellationToken)
    {
        var metadata = await GetJsonAsync(DiscoveryEndpoint, cancellationToken);
        if (!metadata.TryGetProperty("issuer", out var issuer) || issuer.GetString() != Issuer ||
            !metadata.TryGetProperty("jwks_uri", out var jwks) || jwks.GetString() != Issuer + "/.well-known/jwks.json" ||
            !metadata.TryGetProperty("token_endpoint", out var tokenEndpoint) || tokenEndpoint.GetString() != TokenEndpoint)
            throw Failure("invalid_discovery", "OpenAI returned unexpected discovery metadata.");
        return metadata;
    }

    public Task<JsonElement> ReadKeysAsync(CancellationToken cancellationToken) =>
        GetJsonAsync(Issuer + "/.well-known/jwks.json", cancellationToken);

    public async Task<bool> RevokeAsync(ChatGPTAccount account, CancellationToken cancellationToken)
    {
        if (account.Tokens is null) return true;
        var discovery = await ReadDiscoveryAsync(cancellationToken);
        var endpoint = discovery.GetProperty("revocation_endpoint").GetString();
        if (endpoint != IssuerRevocationEndpoint)
            throw Failure("invalid_discovery", "OpenAI returned an unexpected revocation endpoint.");
        for (var attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                using var response = await client.PostAsync(endpoint, new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["token"] = account.Tokens.RefreshToken,
                    ["token_type_hint"] = "refresh_token",
                    ["client_id"] = account.ClientId
                }), cancellationToken);
                if (response.StatusCode == HttpStatusCode.OK) return true;
                if ((int)response.StatusCode < 500) return false;
            }
            catch (HttpRequestException) { }
            if (attempt < 2) await Task.Delay(TimeSpan.FromSeconds(attempt + 1), clock, cancellationToken);
        }
        return false;
    }

    private const string IssuerRevocationEndpoint = Issuer + "/api/accounts/oauth/revoke";

    private async Task<ChatGPTTokens> TokensAsync(Dictionary<string, string> values, ChatGPTTokens? prior,
        CancellationToken cancellationToken)
    {
        using var message = new HttpRequestMessage(HttpMethod.Post, TokenEndpoint) { Content = new FormUrlEncodedContent(values) };
        using var response = await client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        var body = await ReadJsonAsync(response, cancellationToken);
        if (response.StatusCode != HttpStatusCode.OK)
        {
            var code = body.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.String ? error.GetString() : null;
            throw Failure(code is not null && SafeErrorCodes.Contains(code) ? code : "token_exchange_failed",
                $"OpenAI token exchange failed (HTTP {(int)response.StatusCode}); credentials were not logged.");
        }
        try
        {
            if (body.GetProperty("token_type").GetString() is not { } type || !type.Equals("Bearer", StringComparison.OrdinalIgnoreCase))
                throw Failure("invalid_token_response", "OpenAI returned an unsupported token type.");
            var expires = body.GetProperty("expires_in").GetInt32();
            if (expires is <= 0 or > 86400) throw Failure("invalid_token_response", "OpenAI returned invalid token expiry.");
            var access = Required(body, "access_token");
            var refresh = body.TryGetProperty("refresh_token", out var refreshValue) && refreshValue.ValueKind == JsonValueKind.String
                ? refreshValue.GetString() : null;
            if (string.IsNullOrWhiteSpace(refresh)) throw Failure("invalid_token_response", "OpenAI did not return a renewable session.");
            var id = body.TryGetProperty("id_token", out var idValue) && idValue.ValueKind == JsonValueKind.String ? idValue.GetString() : prior?.IdToken;
            if (string.IsNullOrWhiteSpace(id)) throw Failure("invalid_token_response", "OpenAI did not return an identity token.");
            var scopes = body.TryGetProperty("scope", out var scope) ? scope.GetString()!.Split(' ', StringSplitOptions.RemoveEmptyEntries) : prior?.Scopes;
            if (scopes is null) throw Failure("invalid_token_response", "OpenAI did not return granted scopes.");
            DateTimeOffset? earliest = null;
            if (body.TryGetProperty("earliest_refresh_at", out var earliestValue) && earliestValue.ValueKind != JsonValueKind.Null)
                earliest = earliestValue.ValueKind == JsonValueKind.Number
                    ? DateTimeOffset.FromUnixTimeSeconds(earliestValue.GetInt64())
                    : DateTimeOffset.Parse(earliestValue.GetString()!, System.Globalization.CultureInfo.InvariantCulture);
            return new ChatGPTTokens(access, refresh, id, clock.GetUtcNow().AddSeconds(expires), scopes, earliest);
        }
        catch (Exception error) when (error is JsonException or KeyNotFoundException or InvalidOperationException or FormatException or ArgumentOutOfRangeException)
        {
            throw Failure("invalid_token_response", "OpenAI returned an invalid token response.");
        }
    }

    private async Task<JsonElement> GetJsonAsync(string uri, CancellationToken cancellationToken)
    {
        using var response = await client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (!response.IsSuccessStatusCode) throw Failure("discovery_unavailable", "OpenAI authentication metadata is temporarily unavailable.");
        return await ReadJsonAsync(response, cancellationToken);
    }

    private static async Task<JsonElement> ReadJsonAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        await using var input = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var buffer = new MemoryStream();
        var bytes = new byte[8192];
        int count;
        while ((count = await input.ReadAsync(bytes, cancellationToken)) > 0)
        {
            if (buffer.Length + count > 1024 * 1024) throw Failure("invalid_response", "The authentication response exceeded its safety limit.");
            await buffer.WriteAsync(bytes.AsMemory(0, count), cancellationToken);
        }
        try
        {
            using var document = JsonDocument.Parse(buffer.GetBuffer().AsMemory(0, checked((int)buffer.Length)));
            return document.RootElement.Clone();
        }
        catch (JsonException) { throw Failure("invalid_response", "OpenAI returned an invalid authentication response."); }
    }

    private static string Required(JsonElement value, string name) =>
        value.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String &&
        !string.IsNullOrWhiteSpace(property.GetString()) ? property.GetString()! :
        throw Failure("invalid_token_response", "OpenAI returned an incomplete token response.");

    private static ChatGPTAuthenticationException Failure(string code, string message) => new(code, message);
}
