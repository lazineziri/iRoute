using iRoute.Common;
using iRoute.Data;
using iRoute.Tests.Executions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace iRoute.Tests.Contracts;

public sealed class ExecutionStoreContractTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancellationReportsOnlyNewWritesAndPreservesStoreOwnedTimestamp(bool inMemory)
    {
        var token = TestContext.Current.CancellationToken;
        await using var fixture = inMemory ? null : await ExecutionFixture.CreateAsync();
        IExecutionStore store = inMemory ? new InMemoryExecutionStore() : fixture!.Services.GetRequiredService<IExecutionStore>();
        var now = DateTimeOffset.FromUnixTimeMilliseconds(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        var execution = new ExecutionSnapshot(Guid.CreateVersion7(), "email.draft", ExecutionStatus.Running, now, now);
        await store.CreateAsync(execution, null, null, token);
        Assert.True(await store.TryRequestCancellationAsync(execution.ExecutionId, now, token));
        Assert.False(await store.TryRequestCancellationAsync(execution.ExecutionId, now.AddSeconds(1), token));
        await store.UpdateAsync(execution, token);
        Assert.Equal(now, (await store.GetAsync(execution.ExecutionId, token))?.CancellationRequestedAt);
        await store.TryTransitionAsync(execution.ExecutionId, ExecutionStatus.Running, ExecutionStatus.Cancelled, now, token);
        Assert.False(await store.TryRequestCancellationAsync(execution.ExecutionId, now.AddSeconds(2), token));
        Assert.False(await store.TryRequestCancellationAsync(Guid.CreateVersion7(), now, token));
    }
}
