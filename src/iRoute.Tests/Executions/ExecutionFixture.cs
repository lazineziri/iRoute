using System.Text.Json;
using iRoute.Common;
using iRoute.Data;
using iRoute.Runtime.Composition;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace iRoute.Tests.Executions;

internal sealed class ExecutionFixture : IAsyncDisposable
{
    private static readonly string[] DraftDecisions = ["Keep contracts in Common."];
    private readonly DirectoryInfo _directory;
    private readonly IHost _host;
    private readonly AsyncServiceScope _scope;

    private ExecutionFixture(DirectoryInfo directory, IHost host)
    {
        _directory = directory;
        _host = host;
        _scope = host.Services.CreateAsyncScope();
    }

    public IServiceProvider Services => _scope.ServiceProvider;
    public IExecutionService Executions => Services.GetRequiredService<IExecutionService>();

    public static async Task<ExecutionFixture> CreateAsync(IModelGateway? gateway = null, string? model = null, TimeProvider? clock = null,
        Action<IServiceCollection>? configure = null, string? postgresConnectionString = null, string? migrationTarget = null)
    {
        var directory = Directory.CreateTempSubdirectory("iroute-tests-");
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Storage:Provider"] = postgresConnectionString is null ? "Sqlite" : "Postgres",
            ["Storage:AutoInitialize"] = "false",
            ["ConnectionStrings:iRoute"] = postgresConnectionString ?? $"Data Source={Path.Combine(directory.FullName, "runtime.db")};Pooling=False",
            ["ModelGateway:Mode"] = model is null ? "Deterministic" : "OpenAIChatGPT",
            ["ModelGateway:Model"] = model,
            ["ModelGateway:ReasoningEffort"] = model is null ? null : "low",
            ["ModelGateway:SubscriptionTenantId"] = "local",
            ["ModelGateway:Resilience:Enabled"] = "false"
        }).Build();
        var host = new HostBuilder().UseEnvironment("Development")
            .ConfigureServices(services =>
            {
                services.AddLogging();
                services.AddIRouteRuntime(configuration);
                services.AddIRoutePlatform(configuration);
                if (gateway is not null) services.AddSingleton(gateway);
                if (clock is not null) services.AddSingleton(clock);
                configure?.Invoke(services);
            })
            .UseDefaultServiceProvider(options =>
            {
                options.ValidateOnBuild = true;
                options.ValidateScopes = true;
            })
            .Build();
        var fixture = new ExecutionFixture(directory, host);
        try
        {
            await fixture.Services.GetRequiredService<SchemaMigrationManager>().UpgradeAsync(migrationTarget);
            return fixture;
        }
        catch
        {
            await fixture.DisposeAsync();
            throw;
        }
    }

    public static TaskRequest Draft(string key = "draft", string tenant = "tenant") => new(
        "email.draft",
        JsonSerializer.SerializeToElement(new
        {
            recipient = new { name = "Ada" },
            projectName = "iRoute",
            objective = "Review the implementation.",
            activeDecisions = DraftDecisions
        }),
        IdempotencyKey: key,
        TenantId: tenant,
        ActorId: "requester");

    public static TaskRequest Send() => new(
        "email.send",
        JsonSerializer.SerializeToElement(new
        {
            to = "ada@example.com",
            subject = "Review",
            body = "Please review the implementation."
        }),
        IdempotencyKey: "send",
        Constraints: new TaskConstraints(AllowExternalWrites: true, MaxModelCalls: 0, MaxToolCalls: 1),
        TenantId: "tenant",
        ActorId: "requester",
        PermissionScopes: ["email:send"]);

    public async Task<ExecutionSnapshot> ProcessAsync(Guid executionId)
    {
        var queue = Services.GetRequiredService<IExecutionWorkStore>();
        var lease = await queue.TryClaimAsync("test-worker", DateTimeOffset.UtcNow, TimeSpan.FromMinutes(1), default)
            ?? throw new InvalidOperationException("Expected queued work.");
        if (lease.ExecutionId != executionId)
        {
            throw new InvalidOperationException("Claimed a different execution.");
        }

        using var fence = Services.GetRequiredService<IExecutionFence>().Hold(lease.LeaseToken);
        var snapshot = await Executions.ProcessQueuedAsync(executionId, default);
        await queue.CompleteAsync(lease, DateTimeOffset.UtcNow, default);
        return snapshot;
    }

    public async ValueTask DisposeAsync()
    {
        await _scope.DisposeAsync();
        if (_host is IAsyncDisposable disposable)
        {
            await disposable.DisposeAsync();
        }
        else
        {
            _host.Dispose();
        }
        _directory.Delete(recursive: true);
    }
}
