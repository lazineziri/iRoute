using Microsoft.Extensions.Options;

namespace iRoute.Worker;

internal sealed record ExecutionWorkerOptions
{
    public string? WorkerId { get; init; }
    public TimeSpan PollInterval { get; init; } = TimeSpan.FromMilliseconds(250);
    public TimeSpan LeaseDuration { get; init; } = TimeSpan.FromSeconds(30);
    public TimeSpan HeartbeatInterval { get; init; } = TimeSpan.FromSeconds(5);
    public TimeSpan AbandonDelay { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>
    /// Upper bound on the redelivery delay, which doubles with each failed delivery.
    /// </summary>
    public TimeSpan MaxAbandonDelay { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Deliveries allowed before an execution is failed terminally instead of being redelivered.
    /// Zero disables the ceiling and restores unbounded redelivery.
    /// </summary>
    public int MaxDeliveryAttempts { get; init; } = 5;

    public bool HasExhaustedDeliveries(int deliveryAttempt) =>
        MaxDeliveryAttempts > 0 && deliveryAttempt >= MaxDeliveryAttempts;

    /// <summary>
    /// Redelivery delay for a given attempt: doubles per attempt, clamped to
    /// <see cref="MaxAbandonDelay"/>, so one poison item cannot spin the queue.
    /// </summary>
    public TimeSpan AbandonDelayFor(int deliveryAttempt)
    {
        if (deliveryAttempt <= 1)
        {
            return AbandonDelay;
        }

        // Cap the shift before it can overflow the multiplication.
        var doublings = Math.Min(deliveryAttempt - 1, 30);
        var scaled = AbandonDelay * Math.Pow(2, doublings);
        return scaled >= MaxAbandonDelay ? MaxAbandonDelay : scaled;
    }

    public string? ValidationError()
    {
        if (MaxDeliveryAttempts < 0)
        {
            return "ExecutionWorker:MaxDeliveryAttempts cannot be negative; use 0 for unlimited redelivery.";
        }

        if (MaxAbandonDelay < AbandonDelay)
        {
            return "ExecutionWorker:MaxAbandonDelay must be greater than or equal to AbandonDelay.";
        }

        if (PollInterval <= TimeSpan.Zero ||
            LeaseDuration <= TimeSpan.Zero ||
            HeartbeatInterval <= TimeSpan.Zero ||
            AbandonDelay < TimeSpan.Zero)
        {
            return "Execution worker intervals must be positive.";
        }

        if (HeartbeatInterval >= LeaseDuration / 2)
        {
            return "ExecutionWorker:HeartbeatInterval must be less than half of LeaseDuration.";
        }

        return null;
    }
}

internal sealed class ExecutionWorkerOptionsValidator : IValidateOptions<ExecutionWorkerOptions>
{
    public ValidateOptionsResult Validate(string? name, ExecutionWorkerOptions options) =>
        options.ValidationError() is { } error
            ? ValidateOptionsResult.Fail(error)
            : ValidateOptionsResult.Success;
}
