using System.Security.Cryptography;
using System.Text;
using iRoute.Common;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace iRoute.Services;

public sealed class OpenAIIdentityValidator(OpenAIAuthenticationClient authentication, TimeProvider clock) : IDisposable
{
    private readonly SemaphoreSlim _gate = new(1);
    private IEnumerable<SecurityKey>? _keys;
    private DateTimeOffset _fetchedAt;

    public void Dispose() => _gate.Dispose();

    public async Task<ChatGPTIdentity> ValidateAsync(string token, string clientId, string? nonce, CancellationToken cancellationToken)
    {
        if (token.Length > 128 * 1024) throw Invalid();
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var keys = await KeysAsync(attempt > 0, cancellationToken);
            var result = await new JsonWebTokenHandler().ValidateTokenAsync(token, new TokenValidationParameters
            {
                ValidIssuer = OpenAIAuthenticationClient.Issuer,
                ValidAudience = clientId,
                IssuerSigningKeys = keys,
                RequireSignedTokens = true,
                RequireExpirationTime = true,
                ValidateIssuerSigningKey = true,
                ValidateIssuer = true,
                ValidateAudience = true,
                ValidateLifetime = true,
                ValidAlgorithms = [SecurityAlgorithms.RsaSha256],
                ClockSkew = TimeSpan.FromSeconds(5),
                LifetimeValidator = (notBefore, expires, _, _) => expires is { } expiry &&
                    expiry > clock.GetUtcNow().UtcDateTime.AddSeconds(-5) &&
                    (notBefore is null || notBefore <= clock.GetUtcNow().UtcDateTime.AddSeconds(5))
            });
            if (!result.IsValid)
            {
                if (attempt == 0 && result.Exception is SecurityTokenSignatureKeyNotFoundException) continue;
                throw Invalid();
            }
            var claims = result.Claims;
            if (!claims.TryGetValue("sub", out var subject) || subject is not string text || string.IsNullOrWhiteSpace(text) ||
                (nonce is not null && (!claims.TryGetValue("nonce", out var actual) || actual is not string nonceText || !Equal(nonce, nonceText))))
                throw Invalid();
            return new ChatGPTIdentity(text, claims.TryGetValue("email", out var email) ? email as string : null);
        }
        throw Invalid();
    }

    private async Task<IEnumerable<SecurityKey>> KeysAsync(bool force, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (!force && _keys is not null && clock.GetUtcNow() - _fetchedAt < TimeSpan.FromHours(1)) return _keys;
            await authentication.ReadDiscoveryAsync(cancellationToken);
            var keys = await authentication.ReadKeysAsync(cancellationToken);
            _keys = new JsonWebKeySet(keys.GetRawText()).GetSigningKeys();
            _fetchedAt = clock.GetUtcNow();
            return _keys;
        }
        finally { _gate.Release(); }
    }

    internal static bool Equal(string expected, string actual) =>
        CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(expected), Encoding.UTF8.GetBytes(actual));

    private static ChatGPTAuthenticationException Invalid() => new("invalid_identity", "The ChatGPT identity token could not be verified.");
}
