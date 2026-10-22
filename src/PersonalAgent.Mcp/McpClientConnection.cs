using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using PersonalAgent.Core.Agents;

namespace PersonalAgent.Mcp;

public sealed class McpClientConnection(McpConnectionConfig configuration, McpClient client, IClientTransport transport) : IAsyncDisposable
{
    private int disposed;
    private int invalidated;
    private IReadOnlyDictionary<string, string> toolVersions = new Dictionary<string, string>();
    public McpConnectionConfig Configuration { get; } = configuration;
    public string BindingId { get; } = Guid.NewGuid().ToString("N");
    public McpClient Client { get; } = client;
    public bool IsAvailable => Volatile.Read(ref disposed) == 0 && Volatile.Read(ref invalidated) == 0 && !Client.Completion.IsCompleted;
    internal void Invalidate() { Interlocked.Exchange(ref invalidated, 1); UpdateToolVersions([]); }
    internal void UpdateToolVersions(IReadOnlyList<McpDiscoveredTool> tools) =>
        Volatile.Write(ref toolVersions, tools.ToDictionary(tool => tool.Descriptor.Id, tool => tool.Version, StringComparer.Ordinal));
    internal bool IsToolCurrent(McpDiscoveredTool tool) => IsAvailable
        && Volatile.Read(ref toolVersions).TryGetValue(tool.Descriptor.Id, out var version) && version == tool.Version;

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
        Invalidate();
        try
        {
            try { await Client.DisposeAsync().ConfigureAwait(false); }
            finally { await McpConnectionFactory.DisposeTransportAsync(transport).ConfigureAwait(false); }
        }
        catch (Exception) { throw new McpConnectionException("McpCleanupFailed", "MCP resource cleanup could not be confirmed."); }
    }
}
