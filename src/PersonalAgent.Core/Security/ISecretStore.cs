namespace PersonalAgent.Core.Security;

public interface ISecretStore
{
    // Only the generated opaque reference may enter settings/history.
    ValueTask<string> StoreAsync(string secret, CancellationToken cancellationToken = default);
    ValueTask<string?> ReadAsync(string reference, CancellationToken cancellationToken = default);
}
