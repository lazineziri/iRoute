using System.Net;
using System.Text.Json;
using iRoute.Common;
using iRoute.Runtime.Composition;
using iRoute.Services;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;

namespace iRoute.Runtime.Cli;

internal static class ChatGPTAuthCli
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public static async Task<int> RunAsync(string[] args)
    {
        if (args.Length < 2 || args[0] != "chatgpt" || args[1] is "help" or "--help" or "-h")
        {
            Console.WriteLine(HelpText);
            return args.Length == 0 || args[0] == "help" || args[0] == "chatgpt" ? 0 : 2;
        }
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMinutes(10));
        ConsoleCancelEventHandler cancel = (_, signal) => { signal.Cancel = true; cancellation.Cancel(); };
        Console.CancelKeyPress += cancel;
        try
        {
            var accountId = args.Length > 2 && Guid.TryParse(args[2], out var id) ? id : (Guid?)null;
            if (args.Length > 3 || (args.Length == 3 && accountId is null)) throw new ArgumentException("The account ID must be a GUID.");
            if (args[1] == "login") return await LoginAsync(accountId, cancellation.Token);
            var services = new ServiceCollection();
            services.AddSingleton(TimeProvider.System);
            services.AddIRouteChatGPTAuthentication();
            await using var provider = services.BuildServiceProvider();
            var sessions = provider.GetRequiredService<ChatGPTSessionService>();
            switch (args[1])
            {
                case "status":
                case "accounts":
                    await using (var lease = await provider.GetRequiredService<IChatGPTCredentialStore>().AcquireAsync(cancellation.Token))
                        Console.WriteLine(JsonSerializer.Serialize(new
                        {
                            activeAccountId = lease.Document.ActiveAccountId,
                            accounts = lease.Document.Accounts.Select(account => new
                            {
                                account.Id,
                                account.Label,
                                signedIn = account.Tokens is not null,
                                planEnabled = account.Tokens?.PlanEnabled ?? false,
                                expiresAt = account.Tokens?.ExpiresAt
                            })
                        }, JsonOptions));
                    return 0;
                case "select" when accountId is { } selected:
                    await sessions.SelectAsync(selected, cancellation.Token);
                    Console.WriteLine($"Selected ChatGPT account {selected:D}.");
                    return 0;
                case "logout":
                    var revoked = await sessions.LogoutAsync(accountId, cancellation.Token);
                    Console.WriteLine(revoked ? "Signed out; renewable session revoked. Registration retained." :
                        "Signed out locally. Remote revocation was not confirmed; disconnect iRoute in ChatGPT Settings.");
                    return 0;
                default:
                    Console.Error.WriteLine(HelpText);
                    return 2;
            }
        }
        catch (ChatGPTAuthenticationException error)
        {
            Console.Error.WriteLine($"{error.Code}: {error.Message}");
            return 1;
        }
        catch (ArgumentException error)
        {
            Console.Error.WriteLine(error.Message);
            return 2;
        }
        catch (OperationCanceledException)
        {
            Console.Error.WriteLine("Sign-in cancelled or its ten-minute window expired. Start a fresh login.");
            return 1;
        }
        catch (Exception error) when (error is HttpRequestException or IOException or JsonException or InvalidOperationException)
        {
            Console.Error.WriteLine("ChatGPT authentication could not complete. Check connectivity and owner-only credential storage permissions; no credentials were logged.");
            return 1;
        }
        finally { Console.CancelKeyPress -= cancel; }
    }

    private static async Task<int> LoginAsync(Guid? accountId, CancellationToken cancellationToken)
    {
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions
        {
            Args = [],
            EnvironmentName = Environments.Development
        });
        builder.Logging.ClearProviders(); // Callback URLs contain one-time codes: never log them.
        builder.WebHost.ConfigureKestrel(server =>
        {
            server.Listen(IPAddress.Loopback, 0);
            server.Limits.MaxRequestLineSize = 16 * 1024;
            server.Limits.MaxRequestBodySize = 0;
        });
        builder.Services.AddSingleton(TimeProvider.System);
        builder.Services.AddIRouteChatGPTAuthentication();
        await using var app = builder.Build();
        var authorization = app.Services.GetRequiredService<ChatGPTAuthorizationService>();
        var completion = new TaskCompletionSource<ChatGPTAccount>(TaskCreationOptions.RunContinuationsAsynchronously);
        ChatGPTAuthorizationAttempt? attempt = null;
        var accepted = 0;
        app.MapGet("/auth/callback", async (HttpContext context) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            context.Response.Headers["Referrer-Policy"] = "no-referrer";
            context.Response.Headers["Content-Security-Policy"] = "default-src 'none'; style-src 'unsafe-inline'; frame-ancestors 'none'";
            if (attempt is null || context.Connection.RemoteIpAddress is not { } remote || !IPAddress.IsLoopback(remote) ||
                context.Request.Host.Value != new Uri(attempt.RedirectUri).Authority ||
                context.Request.Query.Any(pair => pair.Value.Count != 1) ||
                !context.Request.Query.TryGetValue("state", out var state) || state.ToString() != attempt.State)
            {
                context.Response.StatusCode = 400;
                await context.Response.WriteAsync("This callback does not match the pending iRoute sign-in.", context.RequestAborted);
                return;
            }
            if (Interlocked.Exchange(ref accepted, 1) != 0)
            {
                context.Response.StatusCode = 409;
                return;
            }
            try
            {
                var values = context.Request.Query.ToDictionary(pair => pair.Key, pair => pair.Value.ToString(), StringComparer.Ordinal);
                var account = await authorization.CompleteAsync(attempt, values, cancellationToken);
                completion.TrySetResult(account);
                await context.Response.WriteAsync(account.Tokens?.PlanEnabled == true
                    ? "iRoute sign-in complete. ChatGPT plan access is authorized. You can return to iRoute."
                    : "Identity verified, but ChatGPT plan access was not granted. Review access in ChatGPT Settings.", context.RequestAborted);
            }
            catch (Exception error)
            {
                context.Response.StatusCode = 400;
                completion.TrySetException(error);
                await context.Response.WriteAsync("iRoute could not verify this sign-in. Return to the terminal for a safe error message.", context.RequestAborted);
            }
        });
        await app.StartAsync(cancellationToken); // Listener is ready before exposing the authorization URL.
        try
        {
            var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
            attempt = await authorization.BeginAsync(address + "/auth/callback", accountId, cancellationToken);
            Console.WriteLine("Continue with ChatGPT: open this URL and review the requested identity, offline-session, and ChatGPT plan permissions.");
            Console.WriteLine(attempt.AuthorizationUrl);
            Console.WriteLine("Waiting for your browser callback (10 minutes). No inference requests are made during sign-in.");
            var account = await completion.Task.WaitAsync(cancellationToken);
            Console.WriteLine($"Signed in: {account.Label} ({account.Id:D}). Plan access: {account.Tokens?.PlanEnabled == true}.");
            return 0;
        }
        finally { await app.StopAsync(CancellationToken.None); }
    }

    private const string HelpText = """
        Usage:
          iroute auth chatgpt login [account-id]   Add an account or reauthorize a saved registration
          iroute auth chatgpt accounts             List safe account status (no tokens or email)
          iroute auth chatgpt status               Show the selected account and session status
          iroute auth chatgpt select <account-id>  Select a signed-in account
          iroute auth chatgpt logout [account-id]  Revoke and clear the selected account's tokens

        Credentials: iRoute's own owner-only local files on macOS/Linux; never other applications' sessions.
        Manage plan/credit limits at https://chatgpt.com/settings/usage.
        """;
}
