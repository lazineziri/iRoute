using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using iRoute.Common;
using iRoute.Services;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using Xunit;

namespace iRoute.Tests.Authentication;

public sealed class ChatGPTAuthenticationTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task FirstLoginUsesDynamicRegistrationPkceAndValidatesBeforeSaving()
    {
        using var fixture = new Fixture();
        var attempt = await fixture.BeginAsync();
        var query = QueryHelpers.ParseQuery(new Uri(attempt.AuthorizationUrl).Query);
        Assert.Equal("dynamic_agent_client", query["client_id"]);
        Assert.Equal("iRoute", query["agent_name_hint"]);
        Assert.Equal("S256", query["code_challenge_method"]);
        Assert.Equal(Base64UrlEncoder.Encode(SHA256.HashData(Encoding.ASCII.GetBytes(attempt.Verifier))), query["code_challenge"]);
        Assert.Equal(fixture.Store.Document.HostId, query["ext_agent_host_id"]);
        var account = await fixture.Authorization.CompleteAsync(attempt, Callback(attempt), Token);
        Assert.True(account.Tokens!.PlanEnabled);
        Assert.Equal(account.Id, fixture.Store.Document.ActiveAccountId);
        Assert.Equal("oaiapp_test", fixture.Handler.LastForm["client_id"]);
        Assert.Equal(attempt.RedirectUri, fixture.Handler.LastForm["redirect_uri"]);
        Assert.Equal(attempt.Verifier, fixture.Handler.LastForm["code_verifier"]);
        Assert.DoesNotContain("client_secret", fixture.Handler.LastForm.Keys);
        Assert.DoesNotContain("access-secret", account.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("refresh-secret", account.Tokens.ToString(), StringComparison.Ordinal);
        await Assert.ThrowsAsync<ChatGPTAuthenticationException>(() => fixture.Authorization.CompleteAsync(attempt, Callback(attempt), Token));
        Assert.Equal(1, fixture.Handler.Exchanges);
    }

    [Theory]
    [InlineData("state", "wrong", "invalid_state")]
    [InlineData("client_id", "dynamic_agent_client", "incomplete_registration")]
    [InlineData("client_id", "", "incomplete_registration")]
    [InlineData("code", "", "incomplete_registration")]
    [InlineData("error", "access_denied", "authorization_denied")]
    public async Task UnverifiedCallbacksNeverExchangeCodes(string field, string value, string expected)
    {
        using var fixture = new Fixture();
        var attempt = await fixture.BeginAsync();
        var callback = Callback(attempt);
        callback[field] = value;
        var error = await Assert.ThrowsAsync<ChatGPTAuthenticationException>(() => fixture.Authorization.CompleteAsync(attempt, callback, Token));
        Assert.Equal(expected, error.Code);
        Assert.Equal(0, fixture.Handler.Exchanges);
        Assert.Empty(fixture.Store.Document.Accounts);
    }

    [Theory]
    [InlineData("nonce")]
    [InlineData("audience")]
    [InlineData("issuer")]
    [InlineData("expired")]
    [InlineData("signature")]
    public async Task InvalidIdentityNeverReplacesActiveCredentials(string defect)
    {
        using var fixture = new Fixture();
        fixture.Handler.Defect = defect;
        var attempt = await fixture.BeginAsync();
        var error = await Assert.ThrowsAsync<ChatGPTAuthenticationException>(() => fixture.Authorization.CompleteAsync(attempt, Callback(attempt), Token));
        Assert.Equal("invalid_identity", error.Code);
        Assert.Empty(fixture.Store.Document.Accounts);
        Assert.Null(fixture.Store.Document.ActiveAccountId);
    }

    [Fact]
    public async Task IdentityOnlyGrantIsSavedButCannotPerformPlanInference()
    {
        using var fixture = new Fixture();
        fixture.Handler.Scope = "openid email profile";
        var attempt = await fixture.BeginAsync();
        var account = await fixture.Authorization.CompleteAsync(attempt, Callback(attempt), Token);
        var error = await Assert.ThrowsAsync<ChatGPTAuthenticationException>(() => fixture.Sessions.GetAsync(account.Id, Token));
        Assert.Equal("plan_not_authorized", error.Code);
        Assert.False(account.Tokens!.PlanEnabled);
    }

    [Fact]
    public async Task ReturningLoginRetainsRegistrationAndRejectsAnotherAccount()
    {
        using var fixture = new Fixture();
        var first = await fixture.BeginAsync();
        var saved = await fixture.Authorization.CompleteAsync(first, Callback(first), Token);
        var returning = await fixture.BeginAsync(saved.Id);
        var query = QueryHelpers.ParseQuery(new Uri(returning.AuthorizationUrl).Query);
        Assert.Equal(saved.ClientId, query["client_id"]);
        Assert.False(query.ContainsKey("agent_name_hint"));
        Assert.False(query.ContainsKey("id_token_hint"));
        fixture.Handler.Subject = "another-subject";
        var callback = Callback(returning);
        callback.Remove("client_id");
        var error = await Assert.ThrowsAsync<ChatGPTAuthenticationException>(() => fixture.Authorization.CompleteAsync(returning, callback, Token));
        Assert.Equal("account_mismatch", error.Code);
        Assert.Equal(saved, Assert.Single(fixture.Store.Document.Accounts));
    }

    [Fact]
    public async Task RefreshIsSerializedAndRotatesTheEntireTokenSetOnce()
    {
        using var fixture = new Fixture();
        var attempt = await fixture.BeginAsync();
        var account = await fixture.Authorization.CompleteAsync(attempt, Callback(attempt), Token);
        fixture.Clock.Now = fixture.Clock.Now.AddMinutes(59);
        var results = await Task.WhenAll(Enumerable.Range(0, 5).Select(_ => fixture.Sessions.GetAsync(account.Id, Token)));
        Assert.All(results, token => Assert.Equal("replacement-access", token));
        Assert.Equal(1, fixture.Handler.Refreshes);
        Assert.Equal("replacement-refresh", fixture.Store.Document.Accounts[0].Tokens!.RefreshToken);
        Assert.Equal("oaiapp_test", fixture.Handler.LastForm["client_id"]);
        Assert.Equal("refresh-secret", fixture.Handler.LastForm["refresh_token"]);
        Assert.False(fixture.Handler.LastForm.ContainsKey("scope"));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task RefreshClearsOnlyTerminalFailures(bool terminal)
    {
        using var fixture = new Fixture();
        var attempt = await fixture.BeginAsync();
        var account = await fixture.Authorization.CompleteAsync(attempt, Callback(attempt), Token);
        fixture.Clock.Now = fixture.Clock.Now.AddMinutes(59);
        fixture.Handler.RefreshError = terminal ? "invalid_grant" : "server_error";
        await Assert.ThrowsAsync<ChatGPTAuthenticationException>(() => fixture.Sessions.GetAsync(account.Id, Token));
        Assert.Equal(terminal, fixture.Store.Document.Accounts[0].Tokens is null);
        Assert.Equal(account.ClientId, fixture.Store.Document.Accounts[0].ClientId);
    }

    [Fact]
    public async Task LogoutRevokesAndKeepsTheHostAndRegistration()
    {
        using var fixture = new Fixture();
        var attempt = await fixture.BeginAsync();
        var account = await fixture.Authorization.CompleteAsync(attempt, Callback(attempt), Token);
        Assert.True(await fixture.Sessions.LogoutAsync(account.Id, Token));
        Assert.Equal("refresh-secret", fixture.Handler.LastForm["token"]);
        Assert.Equal("refresh_token", fixture.Handler.LastForm["token_type_hint"]);
        Assert.Null(fixture.Store.Document.Accounts[0].Tokens);
        Assert.Equal(account.ClientId, fixture.Store.Document.Accounts[0].ClientId);
        Assert.Equal(attempt.HostId, fixture.Store.Document.HostId);
    }

    [Theory]
    [InlineData("http://localhost:1455/auth/callback")]
    [InlineData("http://127.0.0.1:1455/callback")]
    [InlineData("http://127.0.0.1:1455/auth/callback?extra=1")]
    public async Task CallbackIdentityCannotChange(string callback)
    {
        using var fixture = new Fixture();
        await Assert.ThrowsAsync<ChatGPTAuthenticationException>(() => fixture.Authorization.BeginAsync(callback, null, Token));
    }

    private static Dictionary<string, string> Callback(ChatGPTAuthorizationAttempt attempt) => new()
    { ["state"] = attempt.State, ["code"] = "test-code", ["client_id"] = "oaiapp_test" };

    private sealed class Fixture : IDisposable
    {
        public MutableClock Clock { get; } = new();
        public MemoryStore Store { get; } = new();
        public OAuthHandler Handler { get; }
        private readonly HttpClient _client;
        private readonly OpenAIIdentityValidator _identities;
        public ChatGPTAuthorizationService Authorization { get; }
        public ChatGPTSessionService Sessions { get; }

        public Fixture()
        {
            Handler = new OAuthHandler(Clock);
            _client = new HttpClient(Handler);
            var authentication = new OpenAIAuthenticationClient(_client, Clock);
            _identities = new OpenAIIdentityValidator(authentication, Clock);
            Authorization = new ChatGPTAuthorizationService(Store, authentication, _identities, Clock);
            Sessions = new ChatGPTSessionService(Store, authentication, _identities, Clock);
        }

        public async Task<ChatGPTAuthorizationAttempt> BeginAsync(Guid? id = null)
        {
            var attempt = await Authorization.BeginAsync("http://127.0.0.1:1455/auth/callback", id, Token);
            Handler.Nonce = attempt.Nonce;
            return attempt;
        }

        public void Dispose() { _identities.Dispose(); _client.Dispose(); Store.Dispose(); }
    }

    private sealed class MutableClock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class MemoryStore : IChatGPTCredentialStore, IDisposable
    {
        private readonly SemaphoreSlim _gate = new(1);
        public ChatGPTCredentialDocument Document { get; private set; } = new($"urn:uuid:{Guid.NewGuid():D}", null, []);
        public async Task<IChatGPTCredentialLease> AcquireAsync(CancellationToken cancellationToken)
        {
            await _gate.WaitAsync(cancellationToken);
            return new Lease(this);
        }
        public void Dispose() => _gate.Dispose();
        private sealed class Lease(MemoryStore store) : IChatGPTCredentialLease
        {
            public ChatGPTCredentialDocument Document => store.Document;
            public Task SaveAsync(ChatGPTCredentialDocument document, CancellationToken cancellationToken)
            { cancellationToken.ThrowIfCancellationRequested(); store.Document = document; return Task.CompletedTask; }
            public ValueTask DisposeAsync() { store._gate.Release(); return ValueTask.CompletedTask; }
        }
    }

    private sealed class OAuthHandler(MutableClock clock) : HttpMessageHandler
    {
        private readonly RSA _rsa = RSA.Create(2048);
        public string Nonce { get; set; } = "";
        public string Subject { get; set; } = "test-subject";
        public string? Defect { get; set; }
        public string Scope { get; set; } = "openid email profile offline_access resource.invoke chatgpt.tokens.use.direct";
        public string? RefreshError { get; set; }
        public Dictionary<string, string> LastForm { get; private set; } = [];
        public int Exchanges { get; private set; }
        public int Refreshes { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("openid-configuration", StringComparison.Ordinal))
                return Json(new
                {
                    issuer = OpenAIAuthenticationClient.Issuer,
                    jwks_uri = OpenAIAuthenticationClient.Issuer + "/.well-known/jwks.json",
                    token_endpoint = OpenAIAuthenticationClient.Issuer + "/api/accounts/oauth/token",
                    revocation_endpoint = OpenAIAuthenticationClient.Issuer + "/api/accounts/oauth/revoke"
                });
            if (request.RequestUri.AbsolutePath.EndsWith("jwks.json", StringComparison.Ordinal))
            {
                var key = _rsa.ExportParameters(false);
                return Json(new { keys = new[] { new { kty = "RSA", kid = "test", alg = "RS256", use = "sig", n = Base64UrlEncoder.Encode(key.Modulus!), e = Base64UrlEncoder.Encode(key.Exponent!) } } });
            }
            LastForm = QueryHelpers.ParseQuery(await request.Content!.ReadAsStringAsync(cancellationToken))
                .ToDictionary(pair => pair.Key, pair => pair.Value.ToString(), StringComparer.Ordinal);
            if (request.RequestUri.AbsolutePath.EndsWith("revoke", StringComparison.Ordinal)) return new HttpResponseMessage(HttpStatusCode.OK);
            var refresh = LastForm["grant_type"] == "refresh_token";
            if (refresh)
            {
                Refreshes++;
                if (RefreshError is not null) return Json(new { error = RefreshError, error_description = "secret-error" }, HttpStatusCode.BadRequest);
            }
            else Exchanges++;
            using var alternate = RSA.Create(2048);
            var token = new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
            {
                Issuer = Defect == "issuer" ? "https://untrusted.example" : OpenAIAuthenticationClient.Issuer,
                Audience = Defect == "audience" ? "another-client" : LastForm["client_id"],
                IssuedAt = clock.Now.UtcDateTime.AddHours(-1),
                NotBefore = clock.Now.UtcDateTime.AddHours(-1),
                Expires = Defect == "expired" ? clock.Now.UtcDateTime.AddMinutes(-5) : clock.Now.UtcDateTime.AddHours(1),
                Claims = new Dictionary<string, object> { ["sub"] = Subject, ["nonce"] = Defect == "nonce" ? "wrong" : Nonce },
                SigningCredentials = new SigningCredentials(new RsaSecurityKey(Defect == "signature" ? alternate : _rsa) { KeyId = "test" }, SecurityAlgorithms.RsaSha256)
            });
            return Json(new
            {
                access_token = refresh ? "replacement-access" : "access-secret",
                refresh_token = refresh ? "replacement-refresh" : "refresh-secret",
                id_token = token,
                token_type = "Bearer",
                expires_in = 3600,
                scope = Scope
            });
        }

        protected override void Dispose(bool disposing) { if (disposing) _rsa.Dispose(); base.Dispose(disposing); }
        private static HttpResponseMessage Json(object value, HttpStatusCode status = HttpStatusCode.OK) =>
            new(status) { Content = new StringContent(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json") };
    }
}
