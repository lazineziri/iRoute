namespace iRoute.Common;

public sealed class ExecutionCommandException(string code, string title, string detail) : Exception(detail)
{
    public string Code { get; } = code;
    public string Title { get; } = title;
}
