using iRoute.Common;

namespace iRoute.Data;

/// <summary>
/// Holds the current lease token in an <see cref="AsyncLocal{T}"/> so it flows through the whole
/// asynchronous processing of one leased execution without being threaded through every call.
/// </summary>
/// <remarks>
/// Concurrent executions on the same worker each get their own value: <see cref="AsyncLocal{T}"/>
/// copies on write, so a nested <see cref="Hold"/> cannot leak into a sibling execution.
/// </remarks>
public sealed class AsyncLocalExecutionFence : IExecutionFence
{
    private static readonly AsyncLocal<Guid?> Token = new();

    public Guid? CurrentToken => Token.Value;

    public IDisposable Hold(Guid leaseToken)
    {
        var previous = Token.Value;
        Token.Value = leaseToken;
        return new Scope(previous);
    }

    private sealed class Scope(Guid? previous) : IDisposable
    {
        private bool _released;

        public void Dispose()
        {
            if (_released)
            {
                return;
            }

            _released = true;
            Token.Value = previous;
        }
    }
}

/// <summary>
/// Used where no lease can be in scope, such as the migration host.
/// </summary>
public sealed class NullExecutionFence : IExecutionFence
{
    public Guid? CurrentToken => null;

    public IDisposable Hold(Guid leaseToken) => new Scope();

    private sealed class Scope : IDisposable
    {
        public void Dispose()
        {
        }
    }
}
