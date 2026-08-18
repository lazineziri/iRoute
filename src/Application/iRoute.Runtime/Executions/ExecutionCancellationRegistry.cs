using System.Collections.Concurrent;
using iRoute.Core;

namespace iRoute.Runtime;

public sealed class ExecutionCancellationRegistry : IExecutionCancellationRegistry, IDisposable
{
    private readonly ConcurrentDictionary<Guid, CancellationTokenSource> _sources = new();

    public CancellationToken Register(Guid executionId, CancellationToken requestCancellation)
    {
        var source = CancellationTokenSource.CreateLinkedTokenSource(requestCancellation);
        if (!_sources.TryAdd(executionId, source))
        {
            source.Dispose();
            throw new InvalidOperationException("Execution cancellation is already registered.");
        }

        return source.Token;
    }

    public bool RequestCancellation(Guid executionId)
    {
        if (!_sources.TryGetValue(executionId, out var source))
        {
            return false;
        }

        source.Cancel();
        return true;
    }

    public void Complete(Guid executionId)
    {
        if (_sources.TryRemove(executionId, out var source))
        {
            source.Dispose();
        }
    }

    public void Dispose()
    {
        foreach (var source in _sources.Values)
        {
            source.Dispose();
        }

        _sources.Clear();
    }
}
