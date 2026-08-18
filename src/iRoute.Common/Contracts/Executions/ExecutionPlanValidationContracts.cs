namespace iRoute.Common;

public sealed record ExecutionPlanValidationIssue(string Code, string Path, string Detail);

public sealed record ExecutionPlanValidationResult(IReadOnlyList<ExecutionPlanValidationIssue> Issues)
{
    public bool IsValid => Issues.Count == 0;
}

public sealed class InvalidExecutionPlanException(IReadOnlyList<ExecutionPlanValidationIssue> issues)
    : Exception(string.Join(" ", issues.Select(issue => $"{issue.Path}: {issue.Detail}")))
{
    public IReadOnlyList<ExecutionPlanValidationIssue> Issues { get; } = issues;
}
