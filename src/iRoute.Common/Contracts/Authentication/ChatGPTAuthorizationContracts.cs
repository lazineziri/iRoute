namespace iRoute.Common;

public sealed record ChatGPTAuthorizationAttempt(string AuthorizationUrl, string State, string Nonce,
    string Verifier, string RedirectUri, string HostId, DateTimeOffset ExpiresAt, ChatGPTAccount? Account)
{
    public override string ToString() => "ChatGPTAuthorizationAttempt { [redacted] }";
}

public sealed record ChatGPTIdentity(string Subject, string? Email);

public sealed class ChatGPTAuthenticationException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}
