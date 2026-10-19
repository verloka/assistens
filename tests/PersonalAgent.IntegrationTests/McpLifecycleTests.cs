using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using PersonalAgent.App;
using PersonalAgent.Core.Agents;
using PersonalAgent.Core.Application;
using PersonalAgent.Core.Domain;
using PersonalAgent.Core.Security;
using PersonalAgent.Mcp;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace PersonalAgent.IntegrationTests;

public sealed class McpLifecycleTests
{
    private static McpLifecycleOptions Options => new(3, TimeSpan.FromMilliseconds(20), TimeSpan.FromMilliseconds(40));

    [Fact]
    public async Task TemporaryConnectionFailureReconnectsWithBoundedBackoffAndDisconnectInvalidatesBindings()
    {
        await using var server = McpTestServer.Program.CreateHttpServer(); await server.StartAsync();
        try
        {
            var provider = new FlakyProvider(new HttpMcpTransportProvider(new Secrets()), 1);
            await using var manager = new McpConnectionManager(new([provider]), new(), Options);
            var config = Http(server.Services);
            var connected = await manager.ConnectAsync(config);
            Assert.Equal(McpConnectionPhase.Connected, connected.Phase); Assert.Equal(2, connected.Attempts); Assert.Equal(2, provider.Attempts);
            var declaration = Assert.Single(connected.Tools);
            var permission = Permission(config.Id, declaration);
            var binding = manager.BindTool(config.Id, permission);
            Assert.Equal(ToolResultStatus.Success, (await binding.ExecuteAsync(Arguments(), Context(), CancellationToken.None)).Status);
            await manager.DisconnectAsync(config.Id);
            Assert.Equal(McpConnectionPhase.Disconnected, Assert.Single(manager.List()).Phase);
            Assert.Equal("McpToolUnavailable", (await binding.ExecuteAsync(Arguments(), Context(), CancellationToken.None)).ErrorCode);
            Assert.Throws<McpConnectionException>(() => manager.BindTool(config.Id, permission));
            await manager.RemoveAsync(config.Id); Assert.Empty(manager.List());
        }
        finally { await server.StopAsync(); }
    }

    [Fact]
    public async Task UnavailableServerAndThrowingObserverDoNotCrashHostOrPreventIndependentLocalTask()
    {
        var provider = new FlakyProvider(new HttpMcpTransportProvider(new Secrets()), int.MaxValue);
        using var host = AppHost.CreateBuilder([]).ConfigureLogging(log => log.ClearProviders()).ConfigureServices(services =>
        {
            services.RemoveAll<IMcpTransportProvider>(); services.AddSingleton<IMcpTransportProvider>(provider);
            services.AddSingleton(Options);
        }).Build();
        await host.StartAsync();
        try
        {
            var manager = host.Services.GetRequiredService<McpConnectionManager>();
            manager.Changed += _ => throw new InvalidOperationException("private-observer-error");
            var result = await manager.ConnectAsync(Http(new Uri("https://unavailable.invalid/mcp")));
            Assert.Equal(McpConnectionPhase.Failed, result.Phase); Assert.Equal(3, result.Attempts); Assert.Equal(3, provider.Attempts);
            Assert.Empty(result.Tools); Assert.Equal("McpServerUnavailable", result.ErrorCode);
            var coordinator = host.Services.GetRequiredService<TaskRunCoordinator>();
            var run = await coordinator.SubmitAsync(new(Guid.NewGuid(), "Independent local diagnostic", new("test")));
            Assert.Equal(RunStatus.Completed, (await coordinator.WaitForTerminalAsync(run.Id).WaitAsync(TimeSpan.FromSeconds(10))).Status);
            Assert.Equal(3, provider.Attempts);
        }
        finally { await host.StopAsync(); }
    }

    [Theory]
    [InlineData("McpAuthenticationFailed")]
    [InlineData("McpConnectionNotApproved")]
    [InlineData("McpToolDiscoveryInvalid")]
    public async Task PolicyAuthenticationAndSchemaErrorsDoNotAutoRetry(string code)
    {
        var provider = new FlakyProvider(new HttpMcpTransportProvider(new Secrets()), int.MaxValue, code);
        await using var manager = new McpConnectionManager(new([provider]), new(), Options);
        var result = await manager.ConnectAsync(Http(new Uri("https://unavailable.invalid/mcp")));
        Assert.Equal(McpConnectionPhase.Failed, result.Phase); Assert.Equal(code, result.ErrorCode); Assert.Equal(1, provider.Attempts);
    }

    [Fact]
    public async Task ActualChildExitTriggersAtMostThreeFreshClientsAndNeverReplaysPreviousTool()
    {
        var log = new Processes();
        var provider = new StdioMcpTransportProvider(new Secrets(), log);
        await using var manager = new McpConnectionManager(new([provider]), new(), Options);
        var config = Stdio();
        var first = await manager.ConnectAsync(config);
        var declaration = Assert.Single(first.Tools);
        var old = manager.BindTool(config.Id, Permission(config.Id, declaration));
        for (var attempt = 1; attempt <= 3; attempt++)
        {
            var pid = log.Pids.Last();
            using var child = Process.GetProcessById(pid);
            child.Kill(entireProcessTree: true); // Only the explicitly launched fixture's PID from our provider logger.
            await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
            if (attempt < 3)
                await Until(() => manager.List().Single().Phase == McpConnectionPhase.Connected && log.Pids.Count == attempt + 1);
            else await Until(() => manager.List().Single().Phase == McpConnectionPhase.Failed);
        }
        Assert.Equal(3, log.Pids.Count); Assert.Empty(Assert.Single(manager.List()).Tools);
        Assert.Equal("McpToolUnavailable", (await old.ExecuteAsync(Arguments(), Context(), CancellationToken.None)).ErrorCode);
        await manager.DisposeAsync();
        foreach (var pid in log.Pids) Assert.False(IsRunning(pid));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DisableOrRemoveDuringActualStdioHandshakeCancelsItAndCleansChildWithoutReconnect(bool remove)
    {
        var log = new Processes();
        await using var manager = new McpConnectionManager(new([new StdioMcpTransportProvider(new Secrets(), log)]), new(), Options);
        var config = Stdio("--fixture-no-protocol");
        var connecting = manager.ConnectAsync(config);
        await Until(() => log.Pids.Count == 1);
        var pid = log.Pids.Single();
        if (remove) await manager.RemoveAsync(config.Id).WaitAsync(TimeSpan.FromSeconds(10));
        else await manager.DisableAsync(config.Id).WaitAsync(TimeSpan.FromSeconds(10));
        await connecting.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.False(IsRunning(pid)); Assert.Single(log.Pids);
        if (remove) Assert.Empty(manager.List());
        else { var status = Assert.Single(manager.List()); Assert.False(status.Enabled); Assert.Equal(McpConnectionPhase.Disconnected, status.Phase); }
    }

    [Fact]
    public async Task RemoveWinsOverManualReconnectAndBothActualChildrenAreReaped()
    {
        var log = new Processes();
        await using var manager = new McpConnectionManager(new([new StdioMcpTransportProvider(new Secrets(), log)]), new(), Options);
        var config = Stdio();
        Assert.Equal(McpConnectionPhase.Connected, (await manager.ConnectAsync(config)).Phase);
        var replacement = Stdio("--fixture-no-protocol") with { Id = config.Id };
        var reconnecting = manager.ConnectAsync(replacement);
        await Until(() => log.Pids.Count == 2);
        await manager.RemoveAsync(config.Id).WaitAsync(TimeSpan.FromSeconds(10));
        await reconnecting.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Empty(manager.List());
        Assert.All(log.Pids, pid => Assert.False(IsRunning(pid)));
    }

    [Fact]
    public async Task HostShutdownDuringActualStdioHandshakeClosesOwnedProcessAndFinishesPendingConnect()
    {
        var log = new Processes();
        using var host = AppHost.CreateBuilder([]).ConfigureLogging(logging => logging.ClearProviders()).ConfigureServices(services =>
        {
            services.RemoveAll<IMcpTransportProvider>(); services.AddSingleton<IMcpTransportProvider>(new StdioMcpTransportProvider(new Secrets(), log));
            services.AddSingleton(Options);
        }).Build();
        await host.StartAsync();
        var manager = host.Services.GetRequiredService<McpConnectionManager>();
        var connecting = manager.ConnectAsync(Stdio("--fixture-no-protocol"));
        await Until(() => log.Pids.Count == 1);
        await host.StopAsync().WaitAsync(TimeSpan.FromSeconds(10));
        await connecting.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Single(log.Pids); Assert.False(IsRunning(log.Pids.Single()));
    }

    [Fact]
    public async Task PeriodicActualDiscoverySeesMetadataChangeAndInvalidatesOldGrantWithoutReplayingTool()
    {
        var description = "Initial remote tool";
        var toolCalls = 0;
        await using var server = McpTestServer.Program.CreateHttpServer(listTools: () => [new Tool { Name = "echo", Description = description,
            InputSchema = JsonSerializer.SerializeToElement(new { type = "object", properties = new { message = new { type = "string" } } }) }],
            callTool: (_, _) => { Interlocked.Increment(ref toolCalls); return ValueTask.FromResult(new CallToolResult { Content = [new TextContentBlock { Text = "hello" }] }); });
        await server.StartAsync();
        try
        {
            await using var manager = new McpConnectionManager(new([new HttpMcpTransportProvider(new Secrets())]), new(), Options);
            var config = Http(server.Services);
            var first = await manager.ConnectAsync(config); var declaration = Assert.Single(first.Tools);
            var binding = manager.BindTool(config.Id, Permission(config.Id, declaration));
            description = "Changed remote tool";
            await Until(() => manager.List().Single().Tools.FirstOrDefault()?.Version != declaration.Version);
            Assert.Equal("McpToolUnavailable", (await binding.ExecuteAsync(Arguments(), Context(), CancellationToken.None)).ErrorCode);
            Assert.Throws<McpConnectionException>(() => manager.BindTool(config.Id, Permission(config.Id, declaration)));
            Assert.Equal(0, toolCalls);
        }
        finally { await server.StopAsync(); }
    }

    private static McpToolPermission Permission(Guid id, McpDiscoveredTool declaration) => new(id, declaration.Descriptor.Id, declaration.Version, ToolRiskLevel.ReadOnly, true);
    private static ToolArguments Arguments() => new(JsonSerializer.SerializeToElement(new { message = "hello" }));
    private static ToolExecutionContext Context() => new(Guid.NewGuid(), Guid.NewGuid(), "diagnostic");
    private static McpConnectionConfig Http(IServiceProvider services) => Http(new Uri(Assert.Single(services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses) + "/mcp"));
    private static McpConnectionConfig Http(Uri uri) => new() { Id = Guid.NewGuid(), Name = "Lifecycle fixture", Enabled = true, Approved = true,
        Transport = McpTransportKind.StreamableHttp, Http = new(uri, AllowLoopbackHttp: uri.IsLoopback), AllowedTools = ["echo"] };
    private static McpConnectionConfig Stdio(params string[] args) => new() { Id = Guid.NewGuid(), Name = "Lifecycle child", Enabled = true, Approved = true,
        Transport = McpTransportKind.Stdio, Stdio = new(Path.GetFullPath(Path.Combine(RuntimeEnvironment.GetRuntimeDirectory(), "..", "..", "..", "dotnet.exe")),
            [typeof(McpTestServer.Program).Assembly.Location, ..args]), AllowedTools = ["echo"] };
    private static bool IsRunning(int pid) { try { using var p = Process.GetProcessById(pid); return !p.HasExited; } catch (ArgumentException) { return false; } }
    private static async Task Until(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        while (!condition()) await Task.Delay(20, timeout.Token);
    }
    private sealed class FlakyProvider(IMcpTransportProvider inner, int failures, string code = "McpServerUnavailable") : IMcpTransportProvider
    {
        public int Attempts;
        public McpTransportKind Transport => inner.Transport;
        public ValueTask<IClientTransport> CreateAsync(McpConnectionConfig configuration, CancellationToken ct)
        {
            if (Interlocked.Increment(ref Attempts) <= failures) throw new McpConnectionException(code, "private-transport-error");
            return inner.CreateAsync(configuration, ct);
        }
    }
    private sealed class Processes : ILogger<StdioMcpTransportProvider>
    {
        public ConcurrentQueue<int> Pids = new();
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel level, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (eventId.Id == 4100 && state is IEnumerable<KeyValuePair<string, object?>> values)
                Pids.Enqueue(Convert.ToInt32(values.Single(pair => pair.Key == "ProcessId").Value));
        }
    }
    private sealed class Secrets : ISecretStore
    {
        public ValueTask<string> StoreAsync(string secret, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask<string?> ReadAsync(string reference, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
