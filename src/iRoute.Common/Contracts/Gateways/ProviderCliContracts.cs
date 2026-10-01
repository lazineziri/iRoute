namespace iRoute.Common;

public sealed record ProviderCliInvocation(
    string Executable, IReadOnlyList<string> Arguments, string StandardInput,
    int DeadlineMilliseconds = 30_000);

public sealed record ProviderCliResult(int ExitCode, string Output);

public interface IProviderCliRunner
{
    Task<ProviderCliResult> RunAsync(ProviderCliInvocation invocation, CancellationToken cancellationToken);
}
