using iRoute.Common;
using iRoute.Data;
using Xunit;

namespace iRoute.Tests.Authentication;

public sealed class ChatGPTCredentialStoreTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task CredentialsAreAtomicOwnerOnlyAndExclusiveAcrossStoreInstances()
    {
        if (OperatingSystem.IsWindows()) return;
        var directory = Path.Combine(OperatingSystem.IsMacOS() ? "/private/tmp" : Path.GetTempPath(), "iroute-auth-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            var first = new ChatGPTFileCredentialStore(directory);
            var second = new ChatGPTFileCredentialStore(directory);
            var lease = await first.AcquireAsync(Token);
            var host = lease.Document.HostId;
            await lease.SaveAsync(new ChatGPTCredentialDocument(host, null, []), Token);
            using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(Token);
            cancellation.CancelAfter(300);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => second.AcquireAsync(cancellation.Token));
            await lease.DisposeAsync();
            await using var reopened = await second.AcquireAsync(Token);
            Assert.Equal(host, reopened.Document.HostId);
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(Path.Combine(directory, "credentials.json")));
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, File.GetUnixFileMode(directory));
            File.SetUnixFileMode(Path.Combine(directory, "credentials.json"), UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead);
            await reopened.DisposeAsync();
            await Assert.ThrowsAsync<IOException>(() => first.AcquireAsync(Token));
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
    }
}
