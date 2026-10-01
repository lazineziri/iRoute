using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using iRoute.Common;
using Microsoft.IdentityModel.Tokens;

namespace iRoute.Services;

public sealed class ChatGPTAuthorizationService(IChatGPTCredentialStore store, OpenAIAuthenticationClient authentication,
    OpenAIIdentityValidator identities, TimeProvider clock)
{
    private readonly ConcurrentDictionary<string, ChatGPTAuthorizationAttempt> _pending = new(StringComparer.Ordinal);

    public async Task<ChatGPTAuthorizationAttempt> BeginAsync(string callbackUri, Guid? accountId, CancellationToken cancellationToken)
    {
        if (!Uri.TryCreate(callbackUri, UriKind.Absolute, out var callback) || callback.Scheme != "http" ||
            callback.Host != "127.0.0.1" || callback.AbsolutePath != "/auth/callback" ||
            !string.IsNullOrEmpty(callback.Query) || !string.IsNullOrEmpty(callback.Fragment) || !string.IsNullOrEmpty(callback.UserInfo))
            throw new ChatGPTAuthenticationException("invalid_callback", "ChatGPT sign-in requires an exact 127.0.0.1 loopback callback.");
        await using var lease = await store.AcquireAsync(cancellationToken);
        var document = lease.Document;
        var account = accountId is { } id ? document.Accounts.SingleOrDefault(item => item.Id == id) : null;
        if (accountId is not null && account is null) throw new ChatGPTAuthenticationException("unknown_account", "Select a saved ChatGPT account or add a new one.");
        await lease.SaveAsync(document, cancellationToken); // Persist the stable host ID before opening the browser.
        var state = Random();
        var nonce = Random();
        var verifier = Random(64);
        var values = new Dictionary<string, string>
        {
            ["client_id"] = account?.ClientId ?? "dynamic_agent_client",
            ["ext_agent_host_id"] = document.HostId,
            ["response_type"] = "code",
            ["redirect_uri"] = callbackUri,
            ["scope"] = "openid profile email offline_access resource.invoke chatgpt.tokens.use.direct",
            ["resource"] = OpenAIAuthenticationClient.Resource,
            ["state"] = state,
            ["nonce"] = nonce,
            ["code_challenge_method"] = "S256",
            ["code_challenge"] = Base64UrlEncoder.Encode(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)))
        };
        if (account is null) values["agent_name_hint"] = "iRoute";
        // Deliberately omit optional token/login hints so printed authorization URLs contain no identity tokens.
        var url = OpenAIAuthenticationClient.Issuer + "/api/accounts/authorize?" + string.Join('&',
            values.Select(pair => Uri.EscapeDataString(pair.Key) + "=" + Uri.EscapeDataString(pair.Value)));
        var attempt = new ChatGPTAuthorizationAttempt(url, state, nonce, verifier, callbackUri, document.HostId,
            clock.GetUtcNow().AddMinutes(10), account);
        foreach (var expired in _pending.Where(pair => pair.Value.ExpiresAt <= clock.GetUtcNow())) _pending.TryRemove(expired.Key, out _);
        _pending[state] = attempt;
        return attempt;
    }

    public async Task<ChatGPTAccount> CompleteAsync(ChatGPTAuthorizationAttempt attempt, IReadOnlyDictionary<string, string> callback,
        CancellationToken cancellationToken)
    {
        if (!callback.TryGetValue("state", out var state) || !OpenAIIdentityValidator.Equal(attempt.State, state) ||
            attempt.ExpiresAt <= clock.GetUtcNow() || !_pending.TryRemove(attempt.State, out var pending) || !ReferenceEquals(pending, attempt))
            throw new ChatGPTAuthenticationException("invalid_state", "The sign-in callback is missing, expired, reused, or does not match this attempt.");
        if (callback.ContainsKey("error")) throw new ChatGPTAuthenticationException("authorization_denied", "ChatGPT sign-in was not approved. No code was exchanged.");
        callback.TryGetValue("client_id", out var clientId);
        if (attempt.Account is { } selected)
        {
            if (clientId is not null && clientId != selected.ClientId) throw new ChatGPTAuthenticationException("account_mismatch", "The callback changed the selected account registration.");
            clientId = selected.ClientId;
        }
        if (string.IsNullOrWhiteSpace(clientId) || !clientId.StartsWith("oaiapp_", StringComparison.Ordinal) || clientId.Length > 512 ||
            !callback.TryGetValue("code", out var code) || string.IsNullOrWhiteSpace(code) || code.Length > 8192)
            throw new ChatGPTAuthenticationException("incomplete_registration", "ChatGPT did not return a complete issued client registration and code.");
        var tokens = await authentication.ExchangeAsync(clientId, code, attempt, cancellationToken);
        var identity = await identities.ValidateAsync(tokens.IdToken, clientId, attempt.Nonce, cancellationToken);
        if (attempt.Account is { } previous && identity.Subject != previous.Subject)
            throw new ChatGPTAuthenticationException("account_mismatch", "The verified identity does not match the selected ChatGPT account.");
        await using var lease = await store.AcquireAsync(cancellationToken);
        if (lease.Document.HostId != attempt.HostId) throw new ChatGPTAuthenticationException("host_mismatch", "The host registration changed during sign-in.");
        var existing = lease.Document.Accounts.SingleOrDefault(item => item.ClientId == clientId);
        if (existing is not null && existing.Subject != identity.Subject)
            throw new ChatGPTAuthenticationException("account_mismatch", "The verified identity does not match the saved registration.");
        var account = new ChatGPTAccount(existing?.Id ?? Guid.NewGuid(), existing?.Label ?? $"ChatGPT {lease.Document.Accounts.Length + 1}",
            clientId, identity.Subject, identity.Email, tokens);
        await lease.SaveAsync(lease.Document with
        {
            ActiveAccountId = account.Id,
            Accounts = lease.Document.Accounts.Where(item => item.Id != account.Id).Append(account).ToArray()
        }, cancellationToken);
        return account;
    }

    private static string Random(int count = 32) => Base64UrlEncoder.Encode(RandomNumberGenerator.GetBytes(count));
}
