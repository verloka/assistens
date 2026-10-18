using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using PersonalAgent.Core.Security;

namespace PersonalAgent.Persistence;

[SupportedOSPlatform("windows")]
public sealed class WindowsDpapiSecretStore(string directory) : ISecretStore
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("PersonalAgent.SecretStore.v1");
    private readonly string root = Path.GetFullPath(directory);

    public async ValueTask<string> StoreAsync(string secret, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(secret);
        cancellationToken.ThrowIfCancellationRequested();
        var clear = Encoding.UTF8.GetBytes(secret);
        byte[]? encrypted = null;
        var reference = "dpapi:" + Guid.NewGuid().ToString("N");
        var path = Resolve(reference);
        var created = false;
        try
        {
            encrypted = ProtectedData.Protect(clear, Entropy, DataProtectionScope.CurrentUser);
            Directory.CreateDirectory(root);
            await using var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, useAsync: true);
            created = true;
            await file.WriteAsync(encrypted, cancellationToken).ConfigureAwait(false);
            await file.FlushAsync(cancellationToken).ConfigureAwait(false);
            return reference;
        }
        catch { if (created && File.Exists(path)) File.Delete(path); throw; }
        finally
        {
            CryptographicOperations.ZeroMemory(clear);
            if (encrypted is not null) CryptographicOperations.ZeroMemory(encrypted);
        }
    }

    public async ValueTask<string?> ReadAsync(string reference, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var path = Resolve(reference);
        byte[] encrypted;
        try { encrypted = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false); }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
        byte[]? clear = null;
        try
        {
            clear = ProtectedData.Unprotect(encrypted, Entropy, DataProtectionScope.CurrentUser);
            cancellationToken.ThrowIfCancellationRequested();
            return Encoding.UTF8.GetString(clear);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(encrypted);
            if (clear is not null) CryptographicOperations.ZeroMemory(clear);
        }
    }

    private string Resolve(string reference)
    {
        if (!reference.StartsWith("dpapi:", StringComparison.Ordinal) || !Guid.TryParseExact(reference[6..], "N", out var id))
            throw new ArgumentException("Invalid secret reference.", nameof(reference));
        return Path.Combine(root, id.ToString("N") + ".secret");
    }
}
