using System.Runtime.CompilerServices;
using System.Text;
using iRoute.Common;

namespace iRoute.Services;

public sealed class QuotaModelGateway(IModelGateway inner, ITenantQuotaStore quotas,
    TenantQuotaOptions options, ModelGatewayDeploymentOptions route, TimeProvider clock) : IModelGateway
{
    public string GatewayId => inner.GatewayId;

    public async Task<ModelGatewayResult> ExecuteAsync(ModelGatewayRequest request, CancellationToken cancellationToken)
    {
        if (!options.Enabled) return await inner.ExecuteAsync(request, cancellationToken);
        var reservation = await ReserveAsync(request, cancellationToken);
        UsageSummary? usage = null;
        using var deadline = new CancellationTokenSource(TimeSpan.FromMilliseconds(request.DeadlineMilliseconds ?? 30000), clock);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token);
        try
        {
            var result = await inner.ExecuteAsync(request, linked.Token);
            usage = result.Usage;
            return result;
        }
        catch (ModelGatewayException error)
        {
            usage = error.ReportedUsage;
            throw;
        }
        finally { await ReconcileAsync(reservation, usage); }
    }

    public async IAsyncEnumerable<ModelGatewayStreamEvent> StreamAsync(ModelGatewayRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (!options.Enabled)
        {
            await foreach (var item in inner.StreamAsync(request, cancellationToken)) yield return item;
            yield break;
        }
        var reservation = await ReserveAsync(request, cancellationToken);
        UsageSummary? usage = null;
        using var deadline = new CancellationTokenSource(TimeSpan.FromMilliseconds(request.DeadlineMilliseconds ?? 30000), clock);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token);
        await using var stream = inner.StreamAsync(request, linked.Token).GetAsyncEnumerator(linked.Token);
        try
        {
            while (true)
            {
                bool advanced;
                try { advanced = await stream.MoveNextAsync(); }
                catch (ModelGatewayException error) { usage = error.ReportedUsage; throw; }
                if (!advanced) break;
                var item = stream.Current;
                if (item.Kind == ModelGatewayStreamEventKind.Completed && item.Result is { } result) usage = result.Usage;
                yield return item;
            }
        }
        finally { await ReconcileAsync(reservation, usage); }
    }

    private async Task<TenantQuotaReservation> ReserveAsync(ModelGatewayRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (request.MaxOutputTokens <= 0 || request.DeadlineMilliseconds is <= 0)
            throw new ArgumentException("Quota-protected model requests require positive output and time budgets.", nameof(request));
        var inputBound = checked((long)Encoding.UTF8.GetByteCount(request.Input.GetRawText()) +
            Encoding.UTF8.GetByteCount(request.Context.GetRawText()) + options.FramingTokenReserve);
        var tokens = checked(inputBound + request.MaxOutputTokens);
        decimal? cost = !ModelGatewayModes.IsSubscription(route.Adapter) && route.InputCostPerMillionTokens is { } inputPrice &&
            route.OutputCostPerMillionTokens is { } outputPrice
            ? (inputBound * inputPrice + request.MaxOutputTokens * outputPrice) / 1000000m : null;
        var now = clock.GetUtcNow();
        var duration = TimeSpan.FromMilliseconds(request.DeadlineMilliseconds ?? 30000);
        var reservation = new TenantQuotaReservation(Guid.CreateVersion7(), request.TenantId, tokens, cost,
            now, now.Add(duration), options.WindowSeconds);
        var permit = await quotas.TryReserveAsync(reservation, options.ForTenant(request.TenantId), cancellationToken);
        if (!permit.Accepted) throw new ModelGatewayException(ErrorCodes.TenantQuotaExceeded,
            $"Tenant model admission was denied by the {permit.Reason} quota.", true,
            failureKind: ModelGatewayFailureKind.RateLimited, gatewayId: GatewayId, correlationId: request.CorrelationId,
            retryAfter: permit.RetryAfter, failureClass: GatewayFailureClass.Policy);
        return reservation;
    }

    private async Task ReconcileAsync(TenantQuotaReservation reservation, UsageSummary? usage)
    {
        // Cancellation, disposal or failure without complete usage retains the reservation, never a fabricated zero.
        using var reconciliation = new CancellationTokenSource(TimeSpan.FromSeconds(10), clock);
        await quotas.ReconcileAsync(reservation.ReservationId, usage, clock.GetUtcNow(), reconciliation.Token);
    }

    public Task<ModelGatewayHealth> CheckHealthAsync(CancellationToken cancellationToken) => inner.CheckHealthAsync(cancellationToken);
}
