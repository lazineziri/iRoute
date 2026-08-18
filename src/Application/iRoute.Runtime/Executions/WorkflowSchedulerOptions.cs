using System.ComponentModel.DataAnnotations;
using Microsoft.Extensions.Options;

namespace iRoute.Runtime;

public sealed record WorkflowSchedulerOptions
{
    [Range(1, int.MaxValue)]
    public int QueueCapacity { get; init; } = 16;

    [Range(1, int.MaxValue)]
    public int MaxParallelSteps { get; init; } = 4;

    [Range(0, int.MaxValue)]
    public int RetryBaseDelayMilliseconds { get; init; } = 100;

    [Range(0, int.MaxValue)]
    public int RetryMaxDelayMilliseconds { get; init; } = 5000;

    [Range(0d, 1d)]
    public double RetryJitterRatio { get; init; } = 0.2;
}

[OptionsValidator]
public sealed partial class WorkflowSchedulerOptionsValidator : IValidateOptions<WorkflowSchedulerOptions>
{
}
