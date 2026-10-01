using iRoute.Common;

namespace iRoute.Services;

public sealed class ChatGPTSessionService(IChatGPTCredentialStore store, OpenAIAuthenticationClient authentication,
    OpenAIIdentityValidator identities, TimeProvider clock) : IChatGPTAccessTokenProvider
{
    public async Task<string> GetAsync(Guid? accountId, CancellationToken cancellationToken)
    {
        await using var lease = await store.AcquireAsync(cancellationToken);
        var account = Select(lease.Document, accountId);
        var tokens = account.Tokens ?? throw Error("sign_in_required", "Run 'iroute auth chatgpt login' to sign in.");
        if (!tokens.PlanEnabled) throw Error("plan_not_authorized", "This account did not grant ChatGPT plan access. Review access in ChatGPT Settings.");
        var now = clock.GetUtcNow();
        if (tokens.ExpiresAt > now.AddMinutes(2)) return tokens.AccessToken;
        if (tokens.EarliestRefreshAt is { } earliest && now < earliest)
        {
            if (tokens.ExpiresAt > now) return tokens.AccessToken;
            throw Error("refresh_not_available", "OpenAI does not allow this session to refresh yet. Try again later.");
        }
        try
        {
            var replacement = await authentication.RefreshAsync(account, cancellationToken);
            if (replacement.IdToken != tokens.IdToken)
            {
                var identity = await identities.ValidateAsync(replacement.IdToken, account.ClientId, null, cancellationToken);
                if (identity.Subject != account.Subject) throw Error("account_mismatch", "Refresh returned a different ChatGPT identity.");
            }
            await ReplaceAsync(lease, account with { Tokens = replacement }, cancellationToken);
            if (!replacement.PlanEnabled) throw Error("plan_not_authorized", "ChatGPT plan permission is no longer granted. Review ChatGPT Settings.");
            return replacement.AccessToken;
        }
        catch (ChatGPTAuthenticationException error) when (error.Code is "invalid_grant" or "invalid_refresh_token" or "token_expired" or
                    "refresh_token_expired" or "refresh_token_invalidated" or "refresh_token_reused")
        {
            await ReplaceAsync(lease, account with { Tokens = null }, cancellationToken);
            throw Error("sign_in_required", "The renewable session is no longer valid. Sign in again using the saved account ID.");
        }
    }

    public async Task SelectAsync(Guid accountId, CancellationToken cancellationToken)
    {
        await using var lease = await store.AcquireAsync(cancellationToken);
        var account = Select(lease.Document, accountId);
        if (account.Tokens is null) throw Error("sign_in_required", "Sign in to this saved account before selecting it.");
        await lease.SaveAsync(lease.Document with { ActiveAccountId = account.Id }, cancellationToken);
    }

    public async Task<bool> LogoutAsync(Guid? accountId, CancellationToken cancellationToken)
    {
        await using var lease = await store.AcquireAsync(cancellationToken);
        var account = Select(lease.Document, accountId);
        var revoked = false;
        try { revoked = await authentication.RevokeAsync(account, cancellationToken); }
        catch (Exception error) when (error is HttpRequestException or ChatGPTAuthenticationException or OperationCanceledException && !cancellationToken.IsCancellationRequested) { }
        await ReplaceAsync(lease, account with { Tokens = null }, cancellationToken);
        return revoked;
    }

    private static ChatGPTAccount Select(ChatGPTCredentialDocument document, Guid? accountId) =>
        document.Accounts.SingleOrDefault(item => item.Id == (accountId ?? document.ActiveAccountId)) ??
        throw Error("sign_in_required", "No selected ChatGPT account. Run 'iroute auth chatgpt login'.");

    private static Task ReplaceAsync(IChatGPTCredentialLease lease, ChatGPTAccount account, CancellationToken cancellationToken) =>
        lease.SaveAsync(lease.Document with { Accounts = lease.Document.Accounts.Select(item => item.Id == account.Id ? account : item).ToArray() }, cancellationToken);

    private static ChatGPTAuthenticationException Error(string code, string message) => new(code, message);
}
