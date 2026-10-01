namespace iRoute.Common;

public sealed class ExternalActionExecutionException(
    string code, string title, string message, bool retryable = false, Exception? innerException = null)
    : Exception(message, innerException)
{
    public string Code { get; } = code;
    public string Title { get; } = title;
    public bool Retryable { get; } = retryable;
}
