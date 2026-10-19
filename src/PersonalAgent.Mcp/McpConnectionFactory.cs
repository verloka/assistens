using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using PersonalAgent.Core.Agents;
using System.Collections.ObjectModel;

namespace PersonalAgent.Mcp;

public sealed class McpConnectionFactory(IEnumerable<IMcpTransportProvider> providers)
{
    private readonly IReadOnlyDictionary<McpTransportKind, IMcpTransportProvider> available = providers.ToDictionary(provider => provider.Transport);

    public async Task<McpClientConnection> ConnectAsync(McpConnectionConfig configuration, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var snapshot = Snapshot(configuration);
        if (!snapshot.Enabled || !snapshot.Approved)
            throw Error("McpConnectionNotApproved", "The MCP connection is disabled or has not been approved by the owner.");
        if (!available.TryGetValue(snapshot.Transport, out var provider))
            throw Error("McpTransportUnavailable", "The configured MCP transport is unavailable.");
        using var timeout = new CancellationTokenSource(snapshot.ConnectTimeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        IClientTransport? transport = null;
        McpClient? client = null;
        try
        {
            transport = await provider.CreateAsync(snapshot, linked.Token).ConfigureAwait(false);
            linked.Token.ThrowIfCancellationRequested();
            client = await McpClient.CreateAsync(transport, new McpClientOptions
            {
                ClientInfo = new Implementation { Name = "PersonalAgent", Version = "1.0" },
                Capabilities = new(),
                InitializationTimeout = snapshot.ConnectTimeout
            }, cancellationToken: linked.Token).ConfigureAwait(false);
            linked.Token.ThrowIfCancellationRequested();
            var connection = new McpClientConnection(snapshot, client, transport);
            client = null; transport = null;
            return connection;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (OperationCanceledException) { throw Error("McpConnectionTimedOut", "The MCP connection timed out; no tool call was retried."); }
        catch (McpConnectionException) { throw; }
        catch (Exception) { throw Error("McpConnectionFailed", "The MCP connection failed; no automatic retry was performed."); }
        finally
        {
            try
            {
                try { if (client is not null) await client.DisposeAsync().ConfigureAwait(false); }
                finally { if (transport is not null) await DisposeTransportAsync(transport).ConfigureAwait(false); }
            }
            catch (Exception) { throw Error("McpCleanupFailed", "MCP resource cleanup could not be confirmed."); }
        }
    }

    private static McpConnectionConfig Snapshot(McpConnectionConfig config)
    {
        if (config.SchemaVersion != 1 || config.Id == Guid.Empty || string.IsNullOrWhiteSpace(config.Name)
            || !Enum.IsDefined(config.Transport) || config.ConnectTimeout <= TimeSpan.Zero || config.ConnectTimeout > TimeSpan.FromMinutes(2)
            || config.RequestTimeout <= TimeSpan.Zero || config.RequestTimeout > TimeSpan.FromMinutes(5)
            || config.AllowedTools is null || config.AllowedTools.Any(string.IsNullOrWhiteSpace))
            throw Error("McpConfigurationInvalid", "The MCP connection configuration is invalid.");
        if (config.Transport == McpTransportKind.Stdio)
        {
            if (config.Stdio is not { } stdio || config.Http is not null || string.IsNullOrWhiteSpace(stdio.Command)
                || stdio.Arguments is null || stdio.Arguments.Any(argument => argument is null || argument.Contains('\0'))
                || stdio.SecretEnvironment?.Any(pair => string.IsNullOrWhiteSpace(pair.Key) || string.IsNullOrWhiteSpace(pair.Value)) == true)
                throw Error("McpConfigurationInvalid", "The stdio MCP configuration is invalid.");
        }
        else if (config.Http is not { } http || config.Stdio is not null || http.Endpoint is not { IsAbsoluteUri: true } endpoint
            || !string.IsNullOrEmpty(endpoint.UserInfo) || !string.IsNullOrEmpty(endpoint.Fragment)
            || endpoint.Scheme != "https" && !(http.AllowLoopbackHttp && endpoint.Scheme == "http" && endpoint.IsLoopback)
            || http.BearerSecretRef is { } reference && string.IsNullOrWhiteSpace(reference))
            throw Error("McpConfigurationInvalid", "The HTTP MCP endpoint or credential scope is invalid.");
        return config with
        {
            AllowedTools = Array.AsReadOnly(config.AllowedTools.Distinct(StringComparer.Ordinal).ToArray()),
            Stdio = config.Stdio is { } value ? value with
            {
                Arguments = Array.AsReadOnly(value.Arguments.ToArray()),
                SecretEnvironment = value.SecretEnvironment is { } environment
                    ? new ReadOnlyDictionary<string, string>(environment.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal)) : null
            } : null
        };
    }

    internal static ValueTask DisposeTransportAsync(IClientTransport transport) => transport is IAsyncDisposable asyncDisposable
        ? asyncDisposable.DisposeAsync() : DisposeSync(transport);
    private static ValueTask DisposeSync(IClientTransport transport) { (transport as IDisposable)?.Dispose(); return ValueTask.CompletedTask; }
    private static McpConnectionException Error(string code, string message) => new(code, message);
}
