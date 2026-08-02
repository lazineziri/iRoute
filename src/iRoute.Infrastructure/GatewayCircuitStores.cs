using System.Data;
using iRoute.Contracts;
using iRoute.Core;
using Microsoft.EntityFrameworkCore;

namespace iRoute.Infrastructure;

public sealed class InMemoryGatewayCircuitStore : IGatewayCircuitStore
{
    private readonly object _gate = new();
    private readonly Dictionary<string, GatewayCircuitSnapshot> _circuits = new(StringComparer.Ordinal);

    public Task<GatewayCircuitPermit> TryAcquireAsync(
        string deploymentId,
        string ownerId,
        GatewayCircuitPolicy policy,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        policy.EnsureValid();
        lock (_gate)
        {
            var current = _circuits.GetValueOrDefault(deploymentId) ?? Initial(deploymentId, now);
            var permit = Acquire(current, ownerId, policy, now);
            _circuits[deploymentId] = permit.Snapshot;
            return Task.FromResult(permit);
        }
    }

    public Task<GatewayCircuitSnapshot> RecordSuccessAsync(
        GatewayCircuitPermit permit,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            var current = _circuits.GetValueOrDefault(permit.DeploymentId) ?? Initial(permit.DeploymentId, now);
            var updated = Success(current, permit, now);
            _circuits[permit.DeploymentId] = updated;
            return Task.FromResult(updated);
        }
    }

    public Task<GatewayCircuitSnapshot> RecordFailureAsync(
        GatewayCircuitPermit permit,
        GatewayFailureClass failureClass,
        bool countsTowardCircuit,
        TimeSpan? retryAfter,
        GatewayCircuitPolicy policy,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        policy.EnsureValid();
        lock (_gate)
        {
            var current = _circuits.GetValueOrDefault(permit.DeploymentId) ?? Initial(permit.DeploymentId, now);
            var updated = Failure(
                current,
                permit,
                failureClass,
                countsTowardCircuit,
                retryAfter,
                policy,
                now);
            _circuits[permit.DeploymentId] = updated;
            return Task.FromResult(updated);
        }
    }

    public Task<IReadOnlyList<GatewayCircuitSnapshot>> ListAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            return Task.FromResult<IReadOnlyList<GatewayCircuitSnapshot>>(
                _circuits.Values.OrderBy(item => item.DeploymentId, StringComparer.Ordinal).ToArray());
        }
    }

    internal static GatewayCircuitSnapshot Initial(string deploymentId, DateTimeOffset now) => new(
        deploymentId,
        GatewayCircuitState.Closed,
        0,
        0,
        null,
        null,
        null,
        null,
        null,
        null,
        null,
        now);

    internal static GatewayCircuitPermit Acquire(
        GatewayCircuitSnapshot current,
        string ownerId,
        GatewayCircuitPolicy policy,
        DateTimeOffset now)
    {
        if (current.State == GatewayCircuitState.Closed)
        {
            return new GatewayCircuitPermit(
                current.DeploymentId,
                ownerId,
                true,
                GatewayCircuitState.Closed,
                null,
                "The deployment circuit is closed.",
                current with { UpdatedAt = now });
        }

        if (current.State == GatewayCircuitState.Open && current.NextProbeAt is { } nextProbe && now < nextProbe)
        {
            return new GatewayCircuitPermit(
                current.DeploymentId,
                ownerId,
                false,
                GatewayCircuitState.Open,
                null,
                $"The deployment circuit is open until {nextProbe:O}.",
                current);
        }

        if (current.State == GatewayCircuitState.HalfOpen &&
            current.ProbeLeaseExpiresAt is { } leaseExpires &&
            now < leaseExpires)
        {
            return new GatewayCircuitPermit(
                current.DeploymentId,
                ownerId,
                false,
                GatewayCircuitState.HalfOpen,
                null,
                "Another worker owns the bounded half-open probe.",
                current);
        }

        var token = Guid.CreateVersion7();
        var halfOpen = current with
        {
            State = GatewayCircuitState.HalfOpen,
            ProbeOwner = ownerId,
            ProbeToken = token,
            ProbeLeaseExpiresAt = now.Add(policy.EffectiveProbeLeaseDuration),
            UpdatedAt = now
        };
        return new GatewayCircuitPermit(
            current.DeploymentId,
            ownerId,
            true,
            GatewayCircuitState.HalfOpen,
            token,
            current.State == GatewayCircuitState.Open
                ? "The open interval elapsed; this worker owns the single half-open probe."
                : "The previous half-open probe lease expired; this worker took over the probe.",
            halfOpen);
    }

    internal static GatewayCircuitSnapshot Success(
        GatewayCircuitSnapshot current,
        GatewayCircuitPermit permit,
        DateTimeOffset now)
    {
        if (permit.State == GatewayCircuitState.HalfOpen && current.ProbeToken != permit.ProbeToken)
        {
            return current;
        }

        if (permit.State == GatewayCircuitState.Closed && current.State != GatewayCircuitState.Closed)
        {
            return current;
        }

        return current with
        {
            State = GatewayCircuitState.Closed,
            ConsecutiveFailures = 0,
            OpenedAt = null,
            NextProbeAt = null,
            ProbeOwner = null,
            ProbeToken = null,
            ProbeLeaseExpiresAt = null,
            UpdatedAt = now
        };
    }

    internal static GatewayCircuitSnapshot Failure(
        GatewayCircuitSnapshot current,
        GatewayCircuitPermit permit,
        GatewayFailureClass failureClass,
        bool countsTowardCircuit,
        TimeSpan? retryAfter,
        GatewayCircuitPolicy policy,
        DateTimeOffset now)
    {
        if (permit.State == GatewayCircuitState.HalfOpen && current.ProbeToken != permit.ProbeToken)
        {
            return current;
        }

        if (permit.State == GatewayCircuitState.Closed && current.State != GatewayCircuitState.Closed)
        {
            return current;
        }

        var observed = current with
        {
            LastFailureClass = failureClass,
            LastFailureAt = now,
            UpdatedAt = now
        };
        if (!countsTowardCircuit)
        {
            return observed;
        }

        var failures = checked(current.ConsecutiveFailures + 1);
        if (permit.State != GatewayCircuitState.HalfOpen && failures < policy.FailureThreshold)
        {
            return observed with { ConsecutiveFailures = failures };
        }

        var openCount = checked(current.OpenCount + 1);
        var multiplier = Math.Pow(2, Math.Min(20, Math.Max(0, openCount - 1)));
        var calculatedTicks = Math.Min(
            policy.EffectiveMaximumOpenDuration.Ticks,
            policy.EffectiveOpenDuration.Ticks * multiplier);
        var delay = TimeSpan.FromTicks((long)calculatedTicks);
        if (retryAfter is { } providerDelay && providerDelay > delay)
        {
            delay = providerDelay > policy.EffectiveMaximumOpenDuration
                ? policy.EffectiveMaximumOpenDuration
                : providerDelay;
        }

        return observed with
        {
            State = GatewayCircuitState.Open,
            ConsecutiveFailures = failures,
            OpenCount = openCount,
            OpenedAt = now,
            NextProbeAt = now.Add(delay),
            ProbeOwner = null,
            ProbeToken = null,
            ProbeLeaseExpiresAt = null
        };
    }
}

public sealed class EfGatewayCircuitStore(IDbContextFactory<IRouteDbContext> contextFactory)
    : IGatewayCircuitStore
{
    public Task<GatewayCircuitPermit> TryAcquireAsync(
        string deploymentId,
        string ownerId,
        GatewayCircuitPolicy policy,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        policy.EnsureValid();
        return MutateAsync(
            deploymentId,
            now,
            current => InMemoryGatewayCircuitStore.Acquire(current, ownerId, policy, now),
            cancellationToken);
    }

    public Task<GatewayCircuitSnapshot> RecordSuccessAsync(
        GatewayCircuitPermit permit,
        DateTimeOffset now,
        CancellationToken cancellationToken) =>
        MutateAsync(
            permit.DeploymentId,
            now,
            current => InMemoryGatewayCircuitStore.Success(current, permit, now),
            cancellationToken);

    public Task<GatewayCircuitSnapshot> RecordFailureAsync(
        GatewayCircuitPermit permit,
        GatewayFailureClass failureClass,
        bool countsTowardCircuit,
        TimeSpan? retryAfter,
        GatewayCircuitPolicy policy,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        policy.EnsureValid();
        return MutateAsync(
            permit.DeploymentId,
            now,
            current => InMemoryGatewayCircuitStore.Failure(
                current,
                permit,
                failureClass,
                countsTowardCircuit,
                retryAfter,
                policy,
                now),
            cancellationToken);
    }

    public async Task<IReadOnlyList<GatewayCircuitSnapshot>> ListAsync(CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var entities = await context.GatewayCircuits
            .AsNoTracking()
            .OrderBy(item => item.DeploymentId)
            .ToArrayAsync(cancellationToken);
        return entities.Select(ToSnapshot).ToArray();
    }

    private async Task<T> MutateAsync<T>(
        string deploymentId,
        DateTimeOffset now,
        Func<GatewayCircuitSnapshot, T> mutation,
        CancellationToken cancellationToken)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
                var isPostgres = context.Database.ProviderName?.Contains("Npgsql", StringComparison.Ordinal) is true;
                await using var transaction = await context.Database.BeginTransactionAsync(
                    isPostgres ? IsolationLevel.ReadCommitted : IsolationLevel.Serializable,
                    cancellationToken);
                var entity = isPostgres
                    ? await context.GatewayCircuits
                        .FromSqlInterpolated(
                            $"SELECT * FROM \"GatewayCircuits\" WHERE \"DeploymentId\" = {deploymentId} FOR UPDATE")
                        .SingleOrDefaultAsync(cancellationToken)
                    : await context.GatewayCircuits.SingleOrDefaultAsync(
                        item => item.DeploymentId == deploymentId,
                        cancellationToken);
                if (entity is null)
                {
                    entity = ToEntity(InMemoryGatewayCircuitStore.Initial(deploymentId, now));
                    context.GatewayCircuits.Add(entity);
                }

                var result = mutation(ToSnapshot(entity));
                var updated = result switch
                {
                    GatewayCircuitPermit permit => permit.Snapshot,
                    GatewayCircuitSnapshot snapshot => snapshot,
                    _ => throw new InvalidOperationException("Unsupported gateway-circuit mutation result.")
                };
                Apply(updated, entity);
                await context.SaveChangesAsync(cancellationToken);
                await transaction.CommitAsync(cancellationToken);
                return result;
            }
            catch (Exception exception) when (attempt < 6 && IsContention(exception))
            {
                await Task.Delay(TimeSpan.FromMilliseconds(attempt * 5), cancellationToken);
            }
        }
    }

    private static bool IsContention(Exception exception) => exception switch
    {
        DbUpdateException { InnerException: Npgsql.PostgresException { SqlState: "23505" or "40001" } } => true,
        Npgsql.PostgresException { SqlState: "23505" or "40001" } => true,
        Microsoft.Data.Sqlite.SqliteException { SqliteErrorCode: 5 or 6 or 19 } => true,
        _ => false
    };

    private static GatewayCircuitSnapshot ToSnapshot(GatewayCircuitEntity entity) => new(
        entity.DeploymentId,
        entity.State,
        entity.ConsecutiveFailures,
        entity.OpenCount,
        FromUnix(entity.OpenedAtUnixMilliseconds),
        FromUnix(entity.NextProbeAtUnixMilliseconds),
        entity.ProbeOwner,
        entity.ProbeToken,
        FromUnix(entity.ProbeLeaseExpiresAtUnixMilliseconds),
        entity.LastFailureClass,
        FromUnix(entity.LastFailureAtUnixMilliseconds),
        DateTimeOffset.FromUnixTimeMilliseconds(entity.UpdatedAtUnixMilliseconds));

    private static GatewayCircuitEntity ToEntity(GatewayCircuitSnapshot snapshot)
    {
        var entity = new GatewayCircuitEntity();
        Apply(snapshot, entity);
        return entity;
    }

    private static void Apply(GatewayCircuitSnapshot snapshot, GatewayCircuitEntity entity)
    {
        entity.DeploymentId = snapshot.DeploymentId;
        entity.State = snapshot.State;
        entity.ConsecutiveFailures = snapshot.ConsecutiveFailures;
        entity.OpenCount = snapshot.OpenCount;
        entity.OpenedAtUnixMilliseconds = snapshot.OpenedAt?.ToUnixTimeMilliseconds();
        entity.NextProbeAtUnixMilliseconds = snapshot.NextProbeAt?.ToUnixTimeMilliseconds();
        entity.ProbeOwner = snapshot.ProbeOwner;
        entity.ProbeToken = snapshot.ProbeToken;
        entity.ProbeLeaseExpiresAtUnixMilliseconds = snapshot.ProbeLeaseExpiresAt?.ToUnixTimeMilliseconds();
        entity.LastFailureClass = snapshot.LastFailureClass;
        entity.LastFailureAtUnixMilliseconds = snapshot.LastFailureAt?.ToUnixTimeMilliseconds();
        entity.UpdatedAtUnixMilliseconds = snapshot.UpdatedAt.ToUnixTimeMilliseconds();
    }

    private static DateTimeOffset? FromUnix(long? value) =>
        value is null ? null : DateTimeOffset.FromUnixTimeMilliseconds(value.Value);
}
