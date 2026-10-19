using PersonalAgent.Core.Agents;

namespace PersonalAgent.Mcp;

public enum McpConnectionPhase { Disconnected, Connecting, Connected, Reconnecting, Failed }
public sealed record McpConnectionStatus(Guid Id, string Name, bool Enabled, McpConnectionPhase Phase, int Attempts,
    string? ErrorCode, IReadOnlyList<McpDiscoveredTool> Tools);
public sealed record McpLifecycleOptions(int MaxAttempts = 3, TimeSpan? InitialBackoff = null, TimeSpan? HealthInterval = null);

// Owns connection resources only. Reconnection never submits or repeats a tool invocation.
public sealed class McpConnectionManager : IAsyncDisposable, IDisposable
{
    private sealed class Entry(McpConnectionConfig configuration)
    {
        public McpConnectionConfig Configuration = configuration;
        public readonly SemaphoreSlim Gate = new(1, 1);
        public CancellationTokenSource? Generation;
        public Task? Monitor;
        public McpClientConnection? Connection;
        public McpConnectionStatus Status = new(configuration.Id, configuration.Name, configuration.Enabled, McpConnectionPhase.Disconnected, 0, null, []);
    }
    private readonly McpConnectionFactory factory;
    private readonly McpToolDiscovery discovery;
    private readonly McpLifecycleOptions options;
    private readonly object sync = new();
    private readonly Dictionary<Guid, Entry> entries = [];
    private readonly CancellationTokenSource lifetime = new();
    private Task? disposal;
    private bool disposed;
    public event Action<McpConnectionStatus>? Changed;

    public McpConnectionManager(McpConnectionFactory factory, McpToolDiscovery discovery, McpLifecycleOptions? options = null)
    {
        this.factory = factory; this.discovery = discovery; this.options = options ?? new();
        if (this.options.MaxAttempts is < 1 or > 5 || Backoff <= TimeSpan.Zero || Backoff > TimeSpan.FromSeconds(30)
            || HealthInterval <= TimeSpan.Zero || HealthInterval > TimeSpan.FromMinutes(5)) throw new ArgumentException("Invalid MCP lifecycle limits.", nameof(options));
    }
    private TimeSpan Backoff => options.InitialBackoff ?? TimeSpan.FromSeconds(1);
    private TimeSpan HealthInterval => options.HealthInterval ?? TimeSpan.FromSeconds(30);

    public IReadOnlyList<McpConnectionStatus> List()
    {
        lock (sync) return entries.Values.Select(entry => entry.Connection is { IsAvailable: false }
            ? entry.Status with { Phase = McpConnectionPhase.Reconnecting, Tools = [] } : entry.Status).OrderBy(status => status.Name, StringComparer.Ordinal).ToList().AsReadOnly();
    }

    public async Task<McpConnectionStatus> ConnectAsync(McpConnectionConfig configuration, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var snapshot = McpConnectionFactory.Snapshot(configuration);
        Entry entry;
        CancellationTokenSource? previousGeneration;
        CancellationTokenSource generation;
        lock (sync)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (!entries.TryGetValue(snapshot.Id, out entry!)) entries.Add(snapshot.Id, entry = new(snapshot));
            previousGeneration = entry.Generation;
            entry.Generation = generation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
            entry.Connection?.Invalidate();
        }
        previousGeneration?.Cancel();
        await entry.Gate.WaitAsync().ConfigureAwait(false);
        try
        {
            lock (sync)
            {
                ObjectDisposedException.ThrowIf(disposed, this);
                if (!ReferenceEquals(entry.Generation, generation)) return entry.Status;
            }
            if (entry.Monitor is not null) await entry.Monitor.ConfigureAwait(false);
            if (!await CloseAsync(entry).ConfigureAwait(false)) return entry.Status;
            previousGeneration?.Dispose();
            entry.Configuration = snapshot;
            Update(entry, new(snapshot.Id, snapshot.Name, snapshot.Enabled, McpConnectionPhase.Connecting, 0, null, []));
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, generation.Token);
            try
            {
                if (await OpenAsync(entry, linked.Token).ConfigureAwait(false))
                    entry.Monitor = MonitorAsync(entry, generation.Token);
            }
            catch (OperationCanceledException)
            {
                await CloseAsync(entry).ConfigureAwait(false);
                Update(entry, entry.Status with { Phase = McpConnectionPhase.Disconnected, Tools = [], ErrorCode = null });
                cancellationToken.ThrowIfCancellationRequested();
            }
            catch (Exception error)
            {
                await CloseAsync(entry).ConfigureAwait(false);
                Update(entry, entry.Status with { Phase = McpConnectionPhase.Failed, Tools = [],
                    ErrorCode = error is McpConnectionException known ? known.Code : "McpLifecycleFailed" });
            }
            return entry.Status;
        }
        finally { entry.Gate.Release(); }
    }

    public McpAgentTool BindTool(Guid connectionId, McpToolPermission permission)
    {
        lock (sync)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (!entries.TryGetValue(connectionId, out var entry) || entry.Status.Phase != McpConnectionPhase.Connected
                || entry.Connection is not { IsAvailable: true } connection
                || entry.Status.Tools.FirstOrDefault(tool => tool.Descriptor.Id == permission.ToolId) is not { } declaration)
                throw new McpConnectionException("McpToolUnavailable", "The MCP connection or tool is unavailable.");
            return new(connection, declaration, permission);
        }
    }

    public Task DisconnectAsync(Guid id, CancellationToken cancellationToken = default) => StopAsync(id, false, false, cancellationToken);
    public Task DisableAsync(Guid id, CancellationToken cancellationToken = default) => StopAsync(id, true, false, cancellationToken);
    public Task RemoveAsync(Guid id, CancellationToken cancellationToken = default) => StopAsync(id, true, true, cancellationToken);

    private async Task StopAsync(Guid id, bool disable, bool remove, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Entry? entry;
        lock (sync)
        {
            if (!entries.TryGetValue(id, out entry)) return;
            entry.Connection?.Invalidate();
            Cancel(entry.Generation);
        }
        // Once accepted, cleanup completes even if the UI caller cancels its wait.
        await entry.Gate.WaitAsync().ConfigureAwait(false);
        try
        {
            Cancel(entry.Generation);
            if (entry.Monitor is not null) await entry.Monitor.ConfigureAwait(false);
            var clean = await CloseAsync(entry).ConfigureAwait(false);
            if (disable) entry.Configuration = entry.Configuration with { Enabled = false };
            Update(entry, entry.Status with { Enabled = entry.Configuration.Enabled,
                Phase = clean ? McpConnectionPhase.Disconnected : McpConnectionPhase.Failed, Tools = [], ErrorCode = clean ? null : "McpCleanupFailed" });
            if (remove && clean)
            {
                lock (sync) entries.Remove(id);
                await discovery.ForgetAsync(id).ConfigureAwait(false);
            }
        }
        finally { entry.Gate.Release(); }
    }

    private async Task<bool> OpenAsync(Entry entry, CancellationToken ct)
    {
        while (entry.Status.Attempts < options.MaxAttempts)
        {
            ct.ThrowIfCancellationRequested();
            var attempt = entry.Status.Attempts + 1;
            Update(entry, entry.Status with { Phase = attempt == 1 ? McpConnectionPhase.Connecting : McpConnectionPhase.Reconnecting, Attempts = attempt, Tools = [] });
            McpClientConnection? opened = null;
            try
            {
                opened = await factory.ConnectAsync(entry.Configuration, ct).ConfigureAwait(false);
                var result = await discovery.RefreshAsync(opened, ct).ConfigureAwait(false);
                ct.ThrowIfCancellationRequested();
                lock (sync) entry.Connection = opened;
                opened = null;
                Update(entry, entry.Status with { Phase = McpConnectionPhase.Connected, ErrorCode = null, Tools = result.Tools });
                return true;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception error)
            {
                var code = error is McpConnectionException known ? known.Code : "McpConnectionFailed";
                Update(entry, entry.Status with { Phase = McpConnectionPhase.Failed, ErrorCode = code, Tools = [] });
                if (!Retryable(code) || attempt == options.MaxAttempts) return false;
            }
            finally { if (opened is not null) await opened.DisposeAsync().ConfigureAwait(false); }
            await Task.Delay(TimeSpan.FromTicks(Backoff.Ticks * (1L << (attempt - 1))), ct).ConfigureAwait(false);
        }
        Update(entry, entry.Status with { Phase = McpConnectionPhase.Failed, ErrorCode = entry.Status.ErrorCode ?? "McpReconnectLimitReached", Tools = [] });
        return false;
    }

    private async Task MonitorAsync(Entry entry, CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var connection = entry.Connection!;
                await Task.WhenAny(connection.Client.Completion, Task.Delay(HealthInterval, ct)).ConfigureAwait(false);
                ct.ThrowIfCancellationRequested();
                await entry.Gate.WaitAsync(ct).ConfigureAwait(false);
                try
                {
                    if (connection.IsAvailable)
                    {
                        try
                        {
                            var result = await discovery.RefreshAsync(connection, ct).ConfigureAwait(false);
                            Update(entry, entry.Status with { Tools = result.Tools });
                            continue;
                        }
                        catch (OperationCanceledException) { throw; }
                        catch (Exception error)
                        {
                            var code = error is McpConnectionException known ? known.Code : "McpRequestFailed";
                            Update(entry, entry.Status with { Phase = McpConnectionPhase.Reconnecting, ErrorCode = code, Tools = [] });
                            if (!Retryable(code)) { await CloseAsync(entry).ConfigureAwait(false); Update(entry, entry.Status with { Phase = McpConnectionPhase.Failed }); return; }
                        }
                    }
                    connection.Invalidate();
                    Update(entry, entry.Status with { Phase = McpConnectionPhase.Reconnecting, Tools = [] });
                    if (!await CloseAsync(entry).ConfigureAwait(false)) return;
                    await Task.Delay(Backoff, ct).ConfigureAwait(false);
                    if (!await OpenAsync(entry, ct).ConfigureAwait(false)) return;
                }
                finally { entry.Gate.Release(); }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception)
        {
            await CloseAsync(entry).ConfigureAwait(false);
            Update(entry, entry.Status with { Phase = McpConnectionPhase.Failed, Tools = [], ErrorCode = "McpLifecycleFailed" });
        }
    }

    private static bool Retryable(string code) => code is "McpConnectionFailed" or "McpConnectionTimedOut" or "McpServerUnavailable"
        or "McpEndpointUnavailable" or "McpRequestFailed" or "McpRequestTimedOut";

    private async Task<bool> CloseAsync(Entry entry)
    {
        McpClientConnection? connection;
        lock (sync) { connection = entry.Connection; entry.Connection = null; }
        if (connection is null) return true;
        connection.Invalidate();
        try { await connection.DisposeAsync().ConfigureAwait(false); return true; }
        catch (Exception) { Update(entry, entry.Status with { Phase = McpConnectionPhase.Failed, Tools = [], ErrorCode = "McpCleanupFailed" }); return false; }
    }
    private void Update(Entry entry, McpConnectionStatus status)
    {
        lock (sync) entry.Status = status;
        foreach (var callback in Changed?.GetInvocationList() ?? [])
            try { ((Action<McpConnectionStatus>)callback)(status); } catch (Exception) { /* UI observers cannot terminate resource ownership. */ }
    }

    public ValueTask DisposeAsync()
    {
        lock (sync) { disposed = true; return new(disposal ??= DisposeCoreAsync()); }
    }
    private async Task DisposeCoreAsync()
    {
        await lifetime.CancelAsync().ConfigureAwait(false);
        Guid[] ids; lock (sync) ids = entries.Keys.ToArray();
        await Task.WhenAll(ids.Select(id => StopAsync(id, true, false, CancellationToken.None))).ConfigureAwait(false);
        Entry[] remaining; lock (sync) remaining = entries.Values.ToArray();
        foreach (var entry in remaining) entry.Generation?.Dispose();
        lifetime.Dispose();
    }
    private static void Cancel(CancellationTokenSource? source) { try { source?.Cancel(); } catch (ObjectDisposedException) { } }
    public void Dispose() => DisposeAsync().AsTask().GetAwaiter().GetResult();
}
