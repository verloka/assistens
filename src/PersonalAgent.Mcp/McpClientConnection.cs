using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using PersonalAgent.Core.Agents;

namespace PersonalAgent.Mcp;

public sealed class McpClientConnection(McpConnectionConfig configuration, McpClient client, IClientTransport transport) : IAsyncDisposable
{
    private int disposed;
    public McpConnectionConfig Configuration { get; } = configuration;
    public McpClient Client { get; } = client;

    public Task<IList<McpClientTool>> ListToolsAsync(CancellationToken cancellationToken = default) =>
        RequestAsync(ct => Client.ListToolsAsync(cancellationToken: ct), cancellationToken);

    public Task<CallToolResult> CallToolAsync(string name, IReadOnlyDictionary<string, object?> arguments, CancellationToken cancellationToken = default) =>
        RequestAsync(ct => Client.CallToolAsync(name, arguments, cancellationToken: ct), cancellationToken);

    private async Task<T> RequestAsync<T>(Func<CancellationToken, ValueTask<T>> operation, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        using var timeout = new CancellationTokenSource(Configuration.RequestTimeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        try { return await operation(linked.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested) { throw new McpConnectionException("McpRequestTimedOut", "The MCP request timed out; its outcome was not confirmed and no call was replayed."); }
        catch (McpConnectionException) { throw; }
        catch (Exception) { throw new McpConnectionException("McpRequestFailed", "The MCP request failed; its outcome was not confirmed and no call was replayed."); }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        try
        {
            try { await Client.DisposeAsync().ConfigureAwait(false); }
            finally { await McpConnectionFactory.DisposeTransportAsync(transport).ConfigureAwait(false); }
        }
        catch (Exception) { throw new McpConnectionException("McpCleanupFailed", "MCP resource cleanup could not be confirmed."); }
    }
}
