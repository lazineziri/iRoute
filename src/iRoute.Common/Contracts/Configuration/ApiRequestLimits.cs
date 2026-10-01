namespace iRoute.Common;

public sealed record ApiRequestLimits
{
    public long MaxBodyBytes { get; init; } = 1048576;
    public int RequestsPerMinute { get; init; } = 120;

    public void EnsureValid()
    {
        if (MaxBodyBytes is < 1024 or > 16777216 || RequestsPerMinute is < 1 or > 100000)
            throw new InvalidOperationException("API body limits must be 1 KiB–16 MiB and rate limits 1–100000 requests per minute.");
    }
}
