namespace iRoute.Common;

public sealed class ApprovalSubmissionException(string code, string title, string message) : Exception(message)
{
    public string Code { get; } = code;
    public string Title { get; } = title;
}
