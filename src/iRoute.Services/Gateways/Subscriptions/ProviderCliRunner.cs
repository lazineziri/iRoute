using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using iRoute.Common;

namespace iRoute.Services;

public sealed class ProviderCliRunner(TimeProvider clock) : IProviderCliRunner
{
    private const int MaximumOutputCharacters = 2 * 1024 * 1024;
    private static readonly string[] ApiEnvironmentKeys =
        ["OPENAI_API_KEY", "OPENAI_BASE_URL", "ANTHROPIC_API_KEY", "ANTHROPIC_AUTH_TOKEN", "ANTHROPIC_BASE_URL",
         "CLAUDE_CODE_OAUTH_TOKEN", "CLAUDE_CODE_USE_BEDROCK", "CLAUDE_CODE_USE_VERTEX", "CLAUDE_CODE_USE_FOUNDRY"];

    public async Task<ProviderCliResult> RunAsync(ProviderCliInvocation invocation, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentException.ThrowIfNullOrWhiteSpace(invocation.Executable);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(invocation.DeadlineMilliseconds);
        var directory = Directory.CreateTempSubdirectory("iroute-provider-");
        using var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(invocation.DeadlineMilliseconds), clock);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token, cancellationToken);
        using var process = new Process();
        var started = false;
        try
        {
            var start = new ProcessStartInfo(invocation.Executable)
            {
                WorkingDirectory = directory.FullName,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            foreach (var argument in invocation.Arguments)
                start.ArgumentList.Add(argument);
            foreach (var key in ApiEnvironmentKeys) start.Environment.Remove(key);
            foreach (var key in start.Environment.Keys.Where(key => key.StartsWith("ANTHROPIC_", StringComparison.Ordinal)).ToArray())
                start.Environment.Remove(key);
            // Provider-managed login storage remains provider-owned; never read or copy its tokens.
            start.Environment["CLAUDE_CODE_SAFE_MODE"] = "1";
            process.StartInfo = start;
            linked.Token.ThrowIfCancellationRequested();
            started = process.Start();
            if (!started) throw new InvalidOperationException("The provider CLI could not be started.");
            var output = ReadBoundedAsync(process.StandardOutput, linked);
            var errors = ReadBoundedAsync(process.StandardError, linked);
            await process.StandardInput.WriteAsync(invocation.StandardInput.AsMemory(), linked.Token);
            process.StandardInput.Close();
            await Task.WhenAll(output, errors, process.WaitForExitAsync(linked.Token));
            return new ProviderCliResult(process.ExitCode, await output);
        }
        catch (Win32Exception)
        {
            throw new ModelGatewayException(ErrorCodes.ModelGatewayUnavailable,
                "The official provider CLI could not be started. Install it and configure its executable path.", false,
                failureClass: GatewayFailureClass.Permanent);
        }
        catch (IOException)
        {
            throw new ModelGatewayException(ErrorCodes.ModelGatewayUnavailable,
                "The provider CLI input/output channel failed.", false, failureClass: GatewayFailureClass.Permanent);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new ModelGatewayException(ErrorCodes.ModelGatewayUnavailable,
                "The provider CLI exceeded its deadline or output safety limit.", true,
                failureKind: ModelGatewayFailureKind.Timeout, failureClass: GatewayFailureClass.Timeout);
        }
        finally
        {
            if (started && !process.HasExited)
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch (InvalidOperationException) when (process.HasExited)
                {
                    // The process can exit between the ownership check and termination.
                }
                await process.WaitForExitAsync(CancellationToken.None);
            }
            directory.Delete(recursive: true);
        }
    }

    private static async Task<string> ReadBoundedAsync(StreamReader reader, CancellationTokenSource cancellation)
    {
        var output = new StringBuilder();
        var buffer = new char[8192];
        int count;
        while ((count = await reader.ReadAsync(buffer, cancellation.Token)) > 0)
        {
            if (output.Length + count > MaximumOutputCharacters)
            {
                await cancellation.CancelAsync();
                throw new OperationCanceledException(cancellation.Token);
            }
            output.Append(buffer, 0, count);
        }
        return output.ToString();
    }
}
