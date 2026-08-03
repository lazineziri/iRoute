using iRoute.Worker;

namespace iRoute.UnitTests;

/// <summary>
/// A deterministically failing execution used to be reclaimed and abandoned about once per second
/// forever, appending two lease events per cycle and starving workers of useful work.
/// </summary>
public sealed class ExecutionWorkerOptionsTests
{
    [Fact]
    public void DeliveriesAreExhaustedAtTheConfiguredCeiling()
    {
        var options = new ExecutionWorkerOptions { MaxDeliveryAttempts = 3 };

        Assert.False(options.HasExhaustedDeliveries(1));
        Assert.False(options.HasExhaustedDeliveries(2));
        Assert.True(options.HasExhaustedDeliveries(3));
        Assert.True(options.HasExhaustedDeliveries(4));
    }

    [Fact]
    public void AZeroCeilingKeepsRedeliveringForOperatorsWhoWantThat()
    {
        var options = new ExecutionWorkerOptions { MaxDeliveryAttempts = 0 };

        Assert.False(options.HasExhaustedDeliveries(1));
        Assert.False(options.HasExhaustedDeliveries(1000));
    }

    [Fact]
    public void RedeliveryBacksOffWithEachAttempt()
    {
        var options = new ExecutionWorkerOptions
        {
            AbandonDelay = TimeSpan.FromSeconds(1),
            MaxAbandonDelay = TimeSpan.FromMinutes(1)
        };

        var first = options.AbandonDelayFor(1);
        var second = options.AbandonDelayFor(2);
        var third = options.AbandonDelayFor(3);

        Assert.Equal(TimeSpan.FromSeconds(1), first);
        Assert.True(second > first, $"{second} should exceed {first}");
        Assert.True(third > second, $"{third} should exceed {second}");
    }

    [Fact]
    public void RedeliveryBackoffIsCapped()
    {
        var options = new ExecutionWorkerOptions
        {
            AbandonDelay = TimeSpan.FromSeconds(1),
            MaxAbandonDelay = TimeSpan.FromSeconds(10)
        };

        Assert.Equal(TimeSpan.FromSeconds(10), options.AbandonDelayFor(50));
        // A very large attempt count must not overflow into a negative or absurd delay.
        Assert.Equal(TimeSpan.FromSeconds(10), options.AbandonDelayFor(int.MaxValue));
    }

    [Fact]
    public void ACeilingBelowTheBackoffFloorIsRejected()
    {
        var options = new ExecutionWorkerOptions
        {
            AbandonDelay = TimeSpan.FromSeconds(30),
            MaxAbandonDelay = TimeSpan.FromSeconds(5)
        };

        Assert.Throws<InvalidOperationException>(options.EnsureValid);
    }

    [Fact]
    public void ANegativeCeilingIsRejected()
    {
        var options = new ExecutionWorkerOptions { MaxDeliveryAttempts = -1 };

        Assert.Throws<InvalidOperationException>(options.EnsureValid);
    }
}
