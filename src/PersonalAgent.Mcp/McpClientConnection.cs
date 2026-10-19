using ModelContextProtocol.Client;
using PersonalAgent.Core.Agents;

namespace PersonalAgent.Mcp;

public sealed class McpClientConnection(McpConnectionConfig configuration, McpClient client, IClientTransport transport) : IAsyncDisposable
{
    private int disposed;
    public McpConnectionConfig Configuration { get; } = configuration;
    public McpClient Client { get; } = client;

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
