namespace iRoute.Services;

internal sealed class ContextCompilationException(
    string code,
    string title,
    string detail) : Exception(detail)
{
    public string Code { get; } = code;
    public string Title { get; } = title;
}
