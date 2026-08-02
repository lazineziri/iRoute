using System.Collections.Concurrent;
using System.Text.Json;
using iRoute.Contracts;
using iRoute.Core;
using iRoute.Infrastructure;
using Microsoft.Extensions.Configuration;

namespace iRoute.UnitTests;

public sealed class GatewayResilienceTests
{
    [Fact]
    public async Task ConfigurationRegistersMultipleProviderNeutralDeploymentRoutes()
    {
        var registry = new ConfiguredGatewayDeploymentRegistry(
            Microsoft.Extensions.Options.Options.Create(new ModelGatewayOptions
            {
                Mode = "Http",
                Deployments =
                [
                    new ModelGatewayDeploymentOptions
                    {
                        RouteId = "eu-small",
                        GatewayId = "gateway-eu",
                        DeploymentId = "small-v1",
                        Provider = "gateway-vendor-a",
                        Region = "westeurope",
                        Residency = "EUR",
                        ModelVersion = "2026-08",
                        Capabilities = ["text.generation"],
                        ProfileIds = ["small-profile"],
                        ExpectedQuality = 0.85m,
                        EstimatedCost = 0.01m,
                        ExpectedLatencyMilliseconds = 200,
                        Priority = 0,
                        BaseUrl = "https://gateway-a.example.invalid"
                    },
                    new ModelGatewayDeploymentOptions
                    {
                        RouteId = "eu-strong",
                        GatewayId = "gateway-eu-fallback",
                        DeploymentId = "strong-v1",
                        Provider = "gateway-vendor-b",
                        Region = "northeurope",
                        Residency = "EUR",
                        ModelVersion = "2026-07",
                        Capabilities = ["text.generation"],
                        ProfileIds = ["strong-profile"],
                        ExpectedQuality = 0.95m,
                        EstimatedCost = 0.04m,
                        ExpectedLatencyMilliseconds = 400,
                        Priority = 1,
                        BaseUrl = "https://gateway-b.example.invalid"
                    }
                ]
            }));

        var routes = await registry.ListAsync(TestContext.Current.CancellationToken);

        Assert.Collection(
            routes,
            first =>
            {
                Assert.Equal("eu-small", first.RouteId);
                Assert.Equal("gateway-vendor-a", first.Provider);
            },
            second =>
            {
                Assert.Equal("eu-strong", second.RouteId);
                Assert.Equal("gateway-vendor-b", second.Provider);
            });
    }

    [Fact]
    public async Task EnvironmentStyleConfigurationBindsWildcardProfileExactlyOnce()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ModelGateway:Mode"] = "Http",
                ["ModelGateway:Deployments:0:RouteId"] = "primary",
                ["ModelGateway:Deployments:0:GatewayId"] = "gateway-a",
                ["ModelGateway:Deployments:0:DeploymentId"] = "deployment-a",
                ["ModelGateway:Deployments:0:Provider"] = "generic-provider",
                ["ModelGateway:Deployments:0:Region"] = "westeurope",
                ["ModelGateway:Deployments:0:Residency"] = "EUR",
                ["ModelGateway:Deployments:0:ModelVersion"] = "model-v1",
                ["ModelGateway:Deployments:0:Capabilities:0"] = "text.generation",
                ["ModelGateway:Deployments:0:ProfileIds:0"] = "*",
                ["ModelGateway:Deployments:0:ExpectedQuality"] = "0.9",
                ["ModelGateway:Deployments:0:EstimatedCost"] = "0.01",
                ["ModelGateway:Deployments:0:ExpectedLatencyMilliseconds"] = "100",
                ["ModelGateway:Deployments:0:BaseUrl"] = "https://gateway-a.example.invalid"
            })
            .Build();
        var configured = configuration.GetSection("ModelGateway").Get<ModelGatewayOptions>();
        var registry = new ConfiguredGatewayDeploymentRegistry(
            Microsoft.Extensions.Options.Options.Create(Assert.IsType<ModelGatewayOptions>(configured)));

        var route = Assert.Single(await registry.ListAsync(TestContext.Current.CancellationToken));

        Assert.Equal(["text.generation"], route.Capabilities);
        Assert.Equal(["*"], route.ProfileIds);
    }

    [Fact]
    public async Task RateLimitRetryAfterOpensPrimaryAndFallsBackExactlyOnce()
    {
        var clock = new MutableClock(new DateTimeOffset(2026, 8, 2, 10, 0, 0, TimeSpan.Zero));
        var circuits = new InMemoryGatewayCircuitStore();
        var primary = new ScriptedGateway("gateway-a", (_, _) => throw new ModelGatewayException(
            ErrorCodes.ModelGatewayHttpError,
            "throttled",
            true,
            429,
            failureKind: ModelGatewayFailureKind.RateLimited,
            gatewayId: "gateway-a",
            retryAfter: TimeSpan.FromSeconds(30),
            failureClass: GatewayFailureClass.Throttling));
        var secondary = new ScriptedGateway("gateway-b", (_, _) => Task.FromResult(Result("secondary")));
        var gateway = Create(
            [Deployment("primary", "gateway-a", priority: 0), Deployment("secondary", "gateway-b", priority: 1)],
            new Dictionary<string, IModelGateway>
            {
                ["primary"] = primary,
                ["secondary"] = secondary
            },
            circuits,
            clock,
            Options(failureThreshold: 1));

        var result = await gateway.ExecuteAsync(Request(maximumAttempts: 2), TestContext.Current.CancellationToken);

        Assert.Equal("secondary", result.Deployment?.DeploymentId);
        Assert.Equal(1, primary.Calls);
        Assert.Equal(1, secondary.Calls);
        Assert.Equal(2, result.Usage.ModelCalls);
        var trace = Assert.IsType<GatewayResilienceTrace>(result.Resilience);
        Assert.Equal(2, trace.Attempts.Count);
        Assert.Equal(GatewayFailureClass.Throttling, trace.Attempts[0].FailureClass);
        Assert.Equal(30_000, trace.Attempts[0].RetryAfterMilliseconds);
        Assert.NotNull(trace.FallbackReason);
        var primaryCircuit = Assert.Single(
            await circuits.ListAsync(TestContext.Current.CancellationToken),
            item => item.DeploymentId == "primary");
        Assert.Equal(GatewayCircuitState.Open, primaryCircuit.State);
        Assert.True(primaryCircuit.NextProbeAt >= clock.UtcNow.AddSeconds(30));
    }

    [Fact]
    public async Task RepeatedProviderFailuresOpenCircuitAndSubsequentCallsSkipPrimary()
    {
        var clock = new MutableClock(new DateTimeOffset(2026, 8, 2, 10, 0, 0, TimeSpan.Zero));
        var circuits = new InMemoryGatewayCircuitStore();
        var primary = Failing("gateway-a", GatewayFailureClass.Provider, httpStatusCode: 503);
        var secondary = new ScriptedGateway("gateway-b", (_, _) => Task.FromResult(Result("secondary")));
        var gateway = Create(
            [Deployment("primary", "gateway-a", priority: 0), Deployment("secondary", "gateway-b", priority: 1)],
            Clients(("primary", primary), ("secondary", secondary)),
            circuits,
            clock,
            Options(failureThreshold: 2));

        _ = await gateway.ExecuteAsync(Request(), TestContext.Current.CancellationToken);
        _ = await gateway.ExecuteAsync(Request(), TestContext.Current.CancellationToken);
        var third = await gateway.ExecuteAsync(Request(), TestContext.Current.CancellationToken);

        Assert.Equal(2, primary.Calls);
        Assert.Equal(3, secondary.Calls);
        Assert.NotNull(Assert.IsType<GatewayResilienceTrace>(third.Resilience).FallbackReason);
        Assert.Contains(
            Assert.IsType<GatewayResilienceTrace>(third.Resilience).Candidates,
            item => item.Deployment.DeploymentId == "primary" &&
                    !item.Eligible &&
                    item.CircuitState == GatewayCircuitState.Open);
    }

    [Fact]
    public async Task SlowMalformedAndLowQualityDeploymentsFallBackWithinDeadline()
    {
        var clock = new MutableClock(DateTimeOffset.UtcNow);
        var slow = new ScriptedGateway("gateway-slow", async (_, cancellationToken) =>
        {
            await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken);
            return Result("too-late");
        });
        var malformed = new ScriptedGateway("gateway-malformed", (_, _) => Task.FromResult(
            Result("malformed") with { Output = default }));
        var lowQuality = new ScriptedGateway("gateway-low", (_, _) => Task.FromResult(
            Result("low") with { Confidence = 0.4m }));
        var healthy = new ScriptedGateway("gateway-healthy", (_, _) => Task.FromResult(Result("healthy")));
        var gateway = Create(
            [
                Deployment("slow", "gateway-slow", priority: 0),
                Deployment("malformed", "gateway-malformed", priority: 1),
                Deployment("low", "gateway-low", priority: 2),
                Deployment("healthy", "gateway-healthy", priority: 3)
            ],
            Clients(
                ("slow", slow),
                ("malformed", malformed),
                ("low", lowQuality),
                ("healthy", healthy)),
            new InMemoryGatewayCircuitStore(),
            clock,
            Options(maximumAttempts: 4, failureThreshold: 1));

        var result = await gateway.ExecuteAsync(
            Request(deadlineMilliseconds: 500, maximumAttempts: 4),
            TestContext.Current.CancellationToken);

        Assert.Equal("healthy", result.Deployment?.DeploymentId);
        Assert.InRange(result.Usage.DurationMilliseconds, 1, 499);
        Assert.Equal(
            [
                GatewayFailureClass.Timeout,
                GatewayFailureClass.MalformedOutput,
                GatewayFailureClass.Validation,
                null
            ],
            Assert.IsType<GatewayResilienceTrace>(result.Resilience)
                .Attempts.Select(item => item.FailureClass).ToArray());
    }

    [Fact]
    public async Task OnlyOneReplicaOwnsHalfOpenProbeAndSuccessClosesCircuit()
    {
        var clock = new MutableClock(new DateTimeOffset(2026, 8, 2, 10, 0, 0, TimeSpan.Zero));
        var circuits = new InMemoryGatewayCircuitStore();
        var probeStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseProbe = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var primaryCalls = 0;
        int? probeDeadline = null;
        var primary = new ScriptedGateway("gateway-a", async (request, cancellationToken) =>
        {
            var call = Interlocked.Increment(ref primaryCalls);
            if (call == 1)
            {
                throw new ModelGatewayException(
                    ErrorCodes.ModelGatewayHttpError,
                    "provider failed",
                    true,
                    503,
                    failureKind: ModelGatewayFailureKind.Unavailable,
                    gatewayId: "gateway-a",
                    failureClass: GatewayFailureClass.Provider);
            }
            if (call == 2)
            {
                probeDeadline = request.DeadlineMilliseconds;
                probeStarted.SetResult();
                await releaseProbe.Task.WaitAsync(cancellationToken);
            }
            return Result("primary");
        }, countCalls: false);
        var fallbackCount = 0;
        var fallbacksCompleted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondary = new ScriptedGateway("gateway-b", (_, _) =>
        {
            if (Interlocked.Increment(ref fallbackCount) >= 8)
            {
                fallbacksCompleted.TrySetResult();
            }
            return Task.FromResult(Result("secondary"));
        });
        var deployments = new[]
        {
            Deployment("primary", "gateway-a", priority: 0),
            Deployment("secondary", "gateway-b", priority: 1)
        };
        var factory = new Dictionary<string, IModelGateway>
        {
            ["primary"] = primary,
            ["secondary"] = secondary
        };
        var resilience = Options(failureThreshold: 1, openDuration: TimeSpan.FromSeconds(1));
        var first = Create(deployments, factory, circuits, clock, resilience);
        _ = await first.ExecuteAsync(Request(), TestContext.Current.CancellationToken);
        clock.Advance(TimeSpan.FromSeconds(2));

        var probeGateway = Create(deployments, factory, circuits, clock, resilience);
        var probe = probeGateway.ExecuteAsync(Request(), TestContext.Current.CancellationToken);
        await probeStarted.Task.WaitAsync(
            TimeSpan.FromSeconds(5),
            TestContext.Current.CancellationToken);
        var competing = Enumerable.Range(0, 7)
            .Select(_ => Create(deployments, factory, circuits, clock, resilience)
                .ExecuteAsync(Request(), TestContext.Current.CancellationToken))
            .ToArray();
        await fallbacksCompleted.Task.WaitAsync(
            TimeSpan.FromSeconds(5),
            TestContext.Current.CancellationToken);
        releaseProbe.SetResult();
        await Task.WhenAll(competing.Append(probe));

        Assert.Equal(2, primaryCalls);
        Assert.InRange(Assert.IsType<int>(probeDeadline), 1, 2_500);
        var recovered = Assert.Single(
            await circuits.ListAsync(TestContext.Current.CancellationToken),
            item => item.DeploymentId == "primary");
        Assert.Equal(GatewayCircuitState.Closed, recovered.State);
        var afterRecovery = await Create(deployments, factory, circuits, clock, resilience)
            .ExecuteAsync(Request(), TestContext.Current.CancellationToken);
        Assert.Equal("primary", afterRecovery.Deployment?.DeploymentId);
        Assert.Equal(3, primaryCalls);
    }

    [Fact]
    public async Task ExhaustionIsStableNonRetryableAndContainsEveryAttempt()
    {
        var first = Failing("gateway-a", GatewayFailureClass.Transport);
        var second = Failing("gateway-b", GatewayFailureClass.Provider, httpStatusCode: 500);
        var gateway = Create(
            [Deployment("first", "gateway-a", priority: 0), Deployment("second", "gateway-b", priority: 1)],
            Clients(("first", first), ("second", second)),
            new InMemoryGatewayCircuitStore(),
            new MutableClock(DateTimeOffset.UtcNow),
            Options(maximumAttempts: 2));

        var exception = await Assert.ThrowsAsync<ModelGatewayException>(() => gateway.ExecuteAsync(
            Request(maximumAttempts: 2),
            TestContext.Current.CancellationToken));

        Assert.Equal(ErrorCodes.ModelGatewayExhausted, exception.Code);
        Assert.False(exception.Retryable);
        Assert.Equal(2, exception.Resilience?.Attempts.Count);
        Assert.NotNull(exception.Resilience?.ExhaustionReason);
        Assert.Equal(1, first.Calls);
        Assert.Equal(1, second.Calls);
    }

    [Fact]
    public async Task PermanentFailureStopsFallbackAndIsReportedAsExhaustionEvidence()
    {
        var permanent = Failing("gateway-a", GatewayFailureClass.Permanent, httpStatusCode: 401);
        var unusedFallback = new ScriptedGateway(
            "gateway-b",
            (_, _) => Task.FromResult(Result("secondary")));
        var gateway = Create(
            [Deployment("first", "gateway-a", priority: 0), Deployment("second", "gateway-b", priority: 1)],
            Clients(("first", permanent), ("second", unusedFallback)),
            new InMemoryGatewayCircuitStore(),
            new MutableClock(DateTimeOffset.UtcNow),
            Options(maximumAttempts: 2));

        var exception = await Assert.ThrowsAsync<ModelGatewayException>(() => gateway.ExecuteAsync(
            Request(maximumAttempts: 2),
            TestContext.Current.CancellationToken));

        Assert.Equal(ErrorCodes.ModelGatewayExhausted, exception.Code);
        Assert.False(exception.Retryable);
        Assert.Equal(GatewayFailureClass.Permanent, Assert.Single(exception.Resilience!.Attempts).FailureClass);
        Assert.Equal(1, permanent.Calls);
        Assert.Equal(0, unusedFallback.Calls);
    }

    [Fact]
    public async Task DeploymentCannotHideAnInternalRetryFromTheBoundedOwner()
    {
        var duplicateRetry = new ScriptedGateway("gateway-a", (_, _) => Task.FromResult(
            Result("duplicate") with
            {
                Usage = new UsageSummary(10, 5, 0.001m, 5, ModelCalls: 2)
            }));
        var fallback = new ScriptedGateway("gateway-b", (_, _) => Task.FromResult(Result("secondary")));
        var gateway = Create(
            [Deployment("first", "gateway-a", priority: 0), Deployment("second", "gateway-b", priority: 1)],
            Clients(("first", duplicateRetry), ("second", fallback)),
            new InMemoryGatewayCircuitStore(),
            new MutableClock(DateTimeOffset.UtcNow),
            Options(maximumAttempts: 2, failureThreshold: 1));

        var result = await gateway.ExecuteAsync(
            Request(maximumAttempts: 2),
            TestContext.Current.CancellationToken);

        Assert.Equal("second", result.Deployment?.DeploymentId);
        Assert.Equal(2, result.Usage.ModelCalls);
        Assert.Equal(
            GatewayFailureClass.MalformedOutput,
            Assert.IsType<GatewayResilienceTrace>(result.Resilience).Attempts[0].FailureClass);
        Assert.Equal(1, duplicateRetry.Calls);
        Assert.Equal(1, fallback.Calls);
    }

    [Fact]
    public async Task RegionResidencyAttemptAndCostPoliciesRejectUnsafeFallbacks()
    {
        var eu = Failing("gateway-eu", GatewayFailureClass.Provider, httpStatusCode: 503);
        var us = new ScriptedGateway("gateway-us", (_, _) => Task.FromResult(Result("us")));
        var expensive = new ScriptedGateway("gateway-expensive", (_, _) => Task.FromResult(Result("expensive")));
        var gateway = Create(
            [
                Deployment("eu", "gateway-eu", "westeurope", "EUR", estimatedCost: 0.01m, priority: 0),
                Deployment("us", "gateway-us", "eastus", "USA", estimatedCost: 0.01m, priority: 1),
                Deployment("expensive", "gateway-expensive", "westeurope", "EUR", estimatedCost: 0.50m, priority: 2)
            ],
            Clients(("eu", eu), ("us", us), ("expensive", expensive)),
            new InMemoryGatewayCircuitStore(),
            new MutableClock(DateTimeOffset.UtcNow),
            Options(maximumAttempts: 3));

        var exception = await Assert.ThrowsAsync<ModelGatewayException>(() => gateway.ExecuteAsync(
            Request(
                maximumAttempts: 1,
                maximumCost: 0.10m,
                allowedRegions: ["westeurope"],
                residency: "EUR"),
            TestContext.Current.CancellationToken));

        Assert.Equal(ErrorCodes.ModelGatewayExhausted, exception.Code);
        Assert.Equal(1, eu.Calls);
        Assert.Equal(0, us.Calls);
        Assert.Equal(0, expensive.Calls);
        var candidates = Assert.IsType<GatewayResilienceTrace>(exception.Resilience).Candidates;
        Assert.Contains(candidates, item => item.Deployment.DeploymentId == "us" &&
                                            item.FailureClass == GatewayFailureClass.Policy);
        Assert.Contains(candidates, item => item.Deployment.DeploymentId == "expensive" &&
                                            item.FailureClass == GatewayFailureClass.Policy);
    }

    private static ResilientModelGateway Create(
        IEnumerable<GatewayDeployment> deployments,
        IReadOnlyDictionary<string, IModelGateway> clients,
        IGatewayCircuitStore circuits,
        IClock clock,
        GatewayResilienceOptions options) =>
        new(
            new StaticRegistry(deployments.ToArray()),
            new StaticClientFactory(clients),
            circuits,
            clock,
            options);

    private static GatewayDeployment Deployment(
        string id,
        string gatewayId,
        string region = "westeurope",
        string residency = "EUR",
        decimal estimatedCost = 0.01m,
        int priority = 0) =>
        new(
            id,
            gatewayId,
            "generic-provider",
            id,
            region,
            residency,
            "model-v1",
            ["text.generation"],
            ["profile-v1"],
            0.95m,
            estimatedCost,
            10,
            priority);

    private static GatewayResilienceOptions Options(
        int maximumAttempts = 3,
        int failureThreshold = 3,
        TimeSpan? openDuration = null) =>
        new()
        {
            MaximumAttempts = maximumAttempts,
            Circuit = new GatewayCircuitPolicy(
                failureThreshold,
                openDuration ?? TimeSpan.FromSeconds(10),
                TimeSpan.FromMinutes(1),
                TimeSpan.FromSeconds(5))
        };

    private static ModelGatewayRequest Request(
        int deadlineMilliseconds = 1_000,
        int maximumAttempts = 3,
        decimal maximumCost = 1m,
        IReadOnlyList<string>? allowedRegions = null,
        string? residency = null) =>
        new(
            "text.generation",
            JsonSerializer.SerializeToElement(new { objective = "resilience test" }),
            JsonSerializer.SerializeToElement(new { }),
            100,
            Guid.CreateVersion7().ToString(),
            "profile-v1",
            deadlineMilliseconds,
            0.8m,
            maximumCost,
            allowedRegions,
            residency,
            maximumAttempts);

    private static ModelGatewayResult Result(string source) => new(
        JsonSerializer.SerializeToElement(new { subject = source, body = "ok" }),
        new UsageSummary(10, 5, 0.001m, 5, 1),
        0.95m,
        []);

    private static ScriptedGateway Failing(
        string gatewayId,
        GatewayFailureClass failureClass,
        int? httpStatusCode = null) =>
        new(gatewayId, (_, _) => throw new ModelGatewayException(
            ErrorCodes.ModelGatewayHttpError,
            "synthetic gateway failure",
            true,
            httpStatusCode,
            failureKind: failureClass == GatewayFailureClass.Timeout
                ? ModelGatewayFailureKind.Timeout
                : ModelGatewayFailureKind.Unavailable,
            gatewayId: gatewayId,
            failureClass: failureClass));

    private static Dictionary<string, IModelGateway> Clients(
        params (string Id, IModelGateway Client)[] clients) =>
        clients.ToDictionary(item => item.Id, item => item.Client, StringComparer.Ordinal);

    private sealed class StaticRegistry(IReadOnlyList<GatewayDeployment> deployments)
        : IGatewayDeploymentRegistry
    {
        public Task<IReadOnlyList<GatewayDeployment>> ListAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(deployments);
        }
    }

    private sealed class StaticClientFactory(IReadOnlyDictionary<string, IModelGateway> clients)
        : IGatewayDeploymentClientFactory
    {
        public IModelGateway GetClient(GatewayDeployment deployment) => clients[deployment.DeploymentId];
    }

    private sealed class ScriptedGateway(
        string gatewayId,
        Func<ModelGatewayRequest, CancellationToken, Task<ModelGatewayResult>> handler,
        bool countCalls = true) : IModelGateway
    {
        private int _calls;
        public string GatewayId { get; } = gatewayId;
        public int Calls => Volatile.Read(ref _calls);

        public Task<ModelGatewayResult> ExecuteAsync(
            ModelGatewayRequest request,
            CancellationToken cancellationToken)
        {
            if (countCalls)
            {
                Interlocked.Increment(ref _calls);
            }
            return handler(request, cancellationToken);
        }
    }

    private sealed class MutableClock(DateTimeOffset now) : IClock
    {
        public DateTimeOffset UtcNow { get; private set; } = now;
        public void Advance(TimeSpan duration) => UtcNow = UtcNow.Add(duration);
    }
}
