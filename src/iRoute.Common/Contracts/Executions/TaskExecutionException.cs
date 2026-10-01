namespace iRoute.Common;

public sealed class TaskExecutionException(string code, string title, string detail, bool retryable = false) : Exception(detail)
{
    public string Code { get; } = code;
    public string Title { get; } = title;
    public bool Retryable { get; } = retryable;
}
