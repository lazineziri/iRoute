using System.Text.Json;
using iRoute.Common;

namespace iRoute.Data;

/// <summary>Owner-only Unix credential files; never reads another application's credentials.</summary>
public sealed class ChatGPTFileCredentialStore(string? directory = null) : IChatGPTCredentialStore
{
    private const UnixFileMode DirectoryMode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
    private const UnixFileMode FileModeBits = UnixFileMode.UserRead | UnixFileMode.UserWrite;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly string _directory = Path.GetFullPath(directory ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "iroute", "chatgpt"));

    public async Task<IChatGPTCredentialLease> AcquireAsync(CancellationToken cancellationToken)
    {
        if (OperatingSystem.IsWindows())
            throw new ChatGPTAuthenticationException("unsupported_credential_store",
                "Native ChatGPT credential storage currently requires macOS or Linux. Windows storage is not implemented.");
        RejectLinks(_directory);
        Directory.CreateDirectory(_directory, DirectoryMode);
        RequirePermissions(_directory, DirectoryMode);
        var lockPath = Path.Combine(_directory, "session.lock");
        FileStream lease;
        var retries = 0;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            RejectLinks(lockPath);
            if (File.Exists(lockPath)) RequirePermissions(lockPath, FileModeBits);
            try
            {
                lease = new FileStream(lockPath, new FileStreamOptions
                {
                    Mode = FileMode.OpenOrCreate,
                    Access = FileAccess.ReadWrite,
                    Share = FileShare.None,
                    UnixCreateMode = FileModeBits,
                    Options = FileOptions.Asynchronous
                });
                break;
            }
            catch (IOException) when (++retries < 300)
            {
                await Task.Delay(100, cancellationToken);
            }
        }
        try
        {
            var path = Path.Combine(_directory, "credentials.json");
            RejectLinks(path);
            ChatGPTCredentialDocument document;
            if (File.Exists(path))
            {
                RequirePermissions(path, FileModeBits);
                var info = new FileInfo(path);
                if (info.Length > 1024 * 1024) throw new IOException("The credential file exceeds its safety limit.");
                await using var input = File.OpenRead(path);
                document = await JsonSerializer.DeserializeAsync<ChatGPTCredentialDocument>(input, JsonOptions, cancellationToken)
                    ?? throw new IOException("The credential file is invalid.");
                if (!document.HostId.StartsWith("urn:uuid:", StringComparison.Ordinal) ||
                    !Guid.TryParse(document.HostId[9..], out _) || document.Accounts is null ||
                    document.Accounts.Select(account => account.Id).Distinct().Count() != document.Accounts.Length)
                    throw new IOException("The credential file is invalid.");
            }
            else document = new ChatGPTCredentialDocument($"urn:uuid:{Guid.NewGuid():D}", null, []);
            return new CredentialLease(lease, path, document);
        }
        catch
        {
            await lease.DisposeAsync();
            throw;
        }
    }

    private static void RejectLinks(string path)
    {
        for (var current = path; !string.IsNullOrEmpty(current); current = Path.GetDirectoryName(current))
            if (new FileInfo(current).LinkTarget is not null)
                throw new IOException("Symbolic links are not allowed in the credential storage path.");
    }

    private static void RequirePermissions(string path, UnixFileMode expected)
    {
        if (!OperatingSystem.IsWindows() && File.GetUnixFileMode(path) != expected)
            throw new IOException("Credential storage must have owner-only permissions (directory 0700, files 0600).");
    }

    private sealed class CredentialLease(FileStream lease, string path, ChatGPTCredentialDocument initial) : IChatGPTCredentialLease
    {
        public ChatGPTCredentialDocument Document { get; private set; } = initial;

        public async Task SaveAsync(ChatGPTCredentialDocument document, CancellationToken cancellationToken)
        {
            if (OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
            RejectLinks(path);
            var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                await using (var output = new FileStream(temporary, new FileStreamOptions
                {
                    Mode = FileMode.CreateNew,
                    Access = FileAccess.Write,
                    Share = FileShare.None,
                    UnixCreateMode = FileModeBits,
                    Options = FileOptions.Asynchronous | FileOptions.WriteThrough
                }))
                {
                    await JsonSerializer.SerializeAsync(output, document, JsonOptions, cancellationToken);
                    await output.FlushAsync(cancellationToken);
                    output.Flush(flushToDisk: true);
                }
                File.Move(temporary, path, overwrite: true);
                Document = document;
            }
            finally
            {
                if (File.Exists(temporary)) File.Delete(temporary);
            }
        }

        public ValueTask DisposeAsync() => lease.DisposeAsync();
    }
}
