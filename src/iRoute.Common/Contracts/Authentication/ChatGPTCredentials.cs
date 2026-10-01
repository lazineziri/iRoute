namespace iRoute.Common;

public sealed record ChatGPTTokens(string AccessToken, string RefreshToken, string IdToken,
    DateTimeOffset ExpiresAt, string[] Scopes, DateTimeOffset? EarliestRefreshAt = null)
{
    public bool PlanEnabled => Scopes.Contains("chatgpt.tokens.use.direct", StringComparer.Ordinal) &&
                               Scopes.Contains("resource.invoke", StringComparer.Ordinal);
    public override string ToString() => "ChatGPTTokens { [redacted] }";
}

public sealed record ChatGPTAccount(Guid Id, string Label, string ClientId, string Subject,
    string? Email, ChatGPTTokens? Tokens)
{
    public override string ToString() => $"ChatGPTAccount {{ Id = {Id}, credentials = [redacted] }}";
}

public sealed record ChatGPTCredentialDocument(string HostId, Guid? ActiveAccountId, ChatGPTAccount[] Accounts)
{
    public override string ToString() => "ChatGPTCredentialDocument { [redacted] }";
}

public interface IChatGPTCredentialStore
{
    Task<IChatGPTCredentialLease> AcquireAsync(CancellationToken cancellationToken);
}

/// <summary>Exclusive cross-process lease, held throughout refresh and atomic replacement.</summary>
public interface IChatGPTCredentialLease : IAsyncDisposable
{
    ChatGPTCredentialDocument Document { get; }
    Task SaveAsync(ChatGPTCredentialDocument document, CancellationToken cancellationToken);
}

public interface IChatGPTAccessTokenProvider
{
    Task<string> GetAsync(Guid? accountId, CancellationToken cancellationToken);
}
