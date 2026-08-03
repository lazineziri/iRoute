using System.Text.Json;
using iRoute.Contracts;
using iRoute.Core;
using iRoute.Infrastructure;

namespace iRoute.UnitTests;

/// <summary>
/// A worker that stops mid external action leaves its reservation running, because iRoute cannot
/// know whether the side effect happened. Every later attempt is refused so the action is never
/// fired twice, which previously wedged the execution permanently: the code told operators that
/// reconciliation was required and no reconciliation existed.
/// </summary>
public sealed class ExternalActionReconciliationTests
{
    private static readonly Guid ExecutionId = Guid.CreateVersion7();
    private const string Tenant = "tenant-a";
    private const string Reference = "idem-ref-1";

    private static ExternalActionRecord Interrupted(DateTimeOffset now) => new(
        ExecutionId,
        Tenant,
        "send-email",
        "email.send",
        Reference,
        "input-hash",
        ExternalActionStatus.Running,
        now,
        now);

    [Fact]
    public async Task AnInterruptedActionIsReportedAsUnresolved()
    {
        var store = new InMemoryExternalActionStore();
        var now = DateTimeOffset.UtcNow;
        await store.ReserveAsync(Interrupted(now), TestContext.Current.CancellationToken);

        var unresolved = await store.ListUnresolvedAsync(
            Tenant,
            ExecutionId,
            TestContext.Current.CancellationToken);

        var action = Assert.Single(unresolved);
        Assert.Equal("send-email", action.ActionId);
        Assert.Equal(ExternalActionStatus.Running, action.Status);
    }

    [Fact]
    public async Task UnresolvedActionsAreScopedToTheirTenant()
    {
        var store = new InMemoryExternalActionStore();
        var now = DateTimeOffset.UtcNow;
        await store.ReserveAsync(Interrupted(now), TestContext.Current.CancellationToken);

        var otherTenant = await store.ListUnresolvedAsync(
            "tenant-b",
            ExecutionId,
            TestContext.Current.CancellationToken);

        Assert.Empty(otherTenant);
    }

    [Fact]
    public async Task ReconcilingAsSucceededReleasesTheReservationAndReusesTheResult()
    {
        var store = new InMemoryExternalActionStore();
        var now = DateTimeOffset.UtcNow;
        await store.ReserveAsync(Interrupted(now), TestContext.Current.CancellationToken);

        await store.CompleteAsync(
            Tenant,
            Reference,
            new ExternalActionResult(
                JsonSerializer.SerializeToElement(new { reconciled = true }),
                [new EvidenceReference("reconciliation", "actor:operator", ObservedAt: now)]),
            now,
            TestContext.Current.CancellationToken);

        Assert.Empty(await store.ListUnresolvedAsync(
            Tenant,
            ExecutionId,
            TestContext.Current.CancellationToken));

        // A resubmission carrying the same idempotency key now reuses the recorded result rather
        // than firing the irreversible action a second time.
        var replay = await store.ReserveAsync(
            Interrupted(now.AddMinutes(1)),
            TestContext.Current.CancellationToken);
        Assert.Equal(ExternalActionReservationKind.Reused, replay.Kind);
    }

    [Fact]
    public async Task ReconcilingAsFailedReleasesTheReservationForARetry()
    {
        var store = new InMemoryExternalActionStore();
        var now = DateTimeOffset.UtcNow;
        await store.ReserveAsync(Interrupted(now), TestContext.Current.CancellationToken);

        await store.FailAsync(
            Tenant,
            Reference,
            new Problem(
                ErrorCodes.ExternalActionFailed,
                "External action reconciled as failed",
                "The provider confirmed the message was never sent.",
                Retryable: true),
            now,
            TestContext.Current.CancellationToken);

        Assert.Empty(await store.ListUnresolvedAsync(
            Tenant,
            ExecutionId,
            TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task AnUnreconciledActionStillBlocksARetry()
    {
        // The conservative behaviour must survive: without an operator decision, nothing releases
        // the reservation and the action cannot fire twice.
        var store = new InMemoryExternalActionStore();
        var now = DateTimeOffset.UtcNow;
        await store.ReserveAsync(Interrupted(now), TestContext.Current.CancellationToken);

        var replay = await store.ReserveAsync(
            Interrupted(now.AddMinutes(1)),
            TestContext.Current.CancellationToken);

        Assert.Equal(ExternalActionReservationKind.InProgress, replay.Kind);
    }
}
