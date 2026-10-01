namespace iRoute.Data;

public sealed class TenantQuotaAccountEntity
{
    public string TenantId { get; set; } = null!;
    public long Revision { get; set; }
}

public sealed class TenantQuotaReservationEntity
{
    public Guid ReservationId { get; set; }
    public string TenantId { get; set; } = null!;
    public long WindowStartUnixMilliseconds { get; set; }
    public long WindowEndUnixMilliseconds { get; set; }
    public long LeaseExpiresAtUnixMilliseconds { get; set; }
    public long ChargedTokens { get; set; }
    public long? ChargedCostMicroUnits { get; set; }
    public bool UsageUnknown { get; set; } = true;
    public long? CompletedAtUnixMilliseconds { get; set; }
}

public sealed class TenantDispatchEntity
{
    public string TenantId { get; set; } = null!;
    public long LastDispatch { get; set; }
}
