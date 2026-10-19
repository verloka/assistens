using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using PersonalAgent.Core.Agents;
using PersonalAgent.Mcp;
using System.Text.Json;

namespace PersonalAgent.IntegrationTests;

public sealed class McpConnectionConfigTests
{
    [Fact]
    public async Task GenericFactoryAddsTwoConfiguredConnectionsWithPinnedSdkAndImmutableAllowlist()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await using var server = McpTestServer.Program.CreateHttpServer();
        await server.StartAsync(timeout.Token);
        try
        {
            var address = Assert.Single(server.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses);
            var allowed = new List<string> { "echo" };
            var provider = new Provider(McpTransportKind.StreamableHttp, (config, _) =>
            {
                allowed.Add("unapproved_write"); // Mutable source settings cannot alter the in-flight approval snapshot.
                return ValueTask.FromResult<IClientTransport>(new HttpClientTransport(new HttpClientTransportOptions
                { Endpoint = config.Http!.Endpoint, TransportMode = HttpTransportMode.StreamableHttp }));
            });
            var factory = new McpConnectionFactory([provider]);
            var firstConfig = Http(new Uri(address + "/mcp")) with { Name = "First configured source", AllowedTools = allowed };
            await using (var first = await factory.ConnectAsync(firstConfig, timeout.Token))
            {
                Assert.Equal(firstConfig.Id, first.Configuration.Id);
                Assert.Equal(new[] { "echo" }, first.Configuration.AllowedTools);
                var tools = await first.Client.ListToolsAsync(cancellationToken: timeout.Token);
                Assert.Equal("echo", Assert.Single(tools).Name);
                await first.DisposeAsync(); // Connection cleanup is idempotent.
            }
            await using var second = await factory.ConnectAsync(firstConfig with { Id = Guid.NewGuid(), Name = "Another configured source", AllowedTools = [] }, timeout.Token);
            Assert.NotEqual(firstConfig.Id, second.Configuration.Id);
            Assert.Empty(second.Configuration.AllowedTools); // Discovery did not approve even the existing echo tool.
            Assert.Equal(2, provider.Calls);
            Assert.Equal("echo", Assert.Single(await second.Client.ListToolsAsync(cancellationToken: timeout.Token)).Name);
        }
        finally { await server.StopAsync(timeout.Token); }
    }

    [Theory]
    [InlineData("disabled", "McpConnectionNotApproved")]
    [InlineData("unapproved", "McpConnectionNotApproved")]
    [InlineData("schema", "McpConfigurationInvalid")]
    [InlineData("identity", "McpConfigurationInvalid")]
    [InlineData("http", "McpConfigurationInvalid")]
    [InlineData("url-secret", "McpConfigurationInvalid")]
    [InlineData("timeout", "McpConfigurationInvalid")]
    [InlineData("unsupported", "McpTransportUnavailable")]
    public async Task InvalidOrUnapprovedConnectionNeverReachesTransportOrCredentialBoundary(string invalid, string code)
    {
        var provider = new Provider(McpTransportKind.StreamableHttp, (_, _) => throw new InvalidOperationException("Transport must not open"));
        var config = Http(new Uri("https://example.invalid/mcp"));
        config = invalid switch
        {
            "disabled" => config with { Enabled = false }, "unapproved" => config with { Approved = false },
            "schema" => config with { SchemaVersion = 999 }, "identity" => config with { Id = Guid.Empty },
            "http" => config with { Http = new(new Uri("http://example.invalid/mcp")) },
            "url-secret" => config with { Http = new(new Uri("https://user:private-test-secret@example.invalid/mcp")) },
            "timeout" => config with { ConnectTimeout = TimeSpan.Zero },
            _ => config with { Transport = McpTransportKind.Stdio, Http = null, Stdio = new("test-command", []) }
        };
        var error = await Assert.ThrowsAsync<McpConnectionException>(() => new McpConnectionFactory([provider]).ConnectAsync(config));
        Assert.Equal(code, error.Code); Assert.Equal(0, provider.Calls);
        Assert.DoesNotContain("private-test-secret", error.Message);
    }

    [Fact]
    public async Task CancellationAndTimeoutBoundTransportCreationWithoutRetryOrRawError()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var provider = new Provider(McpTransportKind.StreamableHttp, async (_, ct) =>
        { entered.TrySetResult(); await Task.Delay(Timeout.InfiniteTimeSpan, ct); throw new InvalidOperationException(); });
        var factory = new McpConnectionFactory([provider]);
        var config = Http(new Uri("https://example.invalid/mcp"));
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => factory.ConnectAsync(config, cancelled.Token));
        Assert.Equal(0, provider.Calls);
        using var stop = new CancellationTokenSource();
        var connection = factory.ConnectAsync(config, stop.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5)); stop.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => connection.WaitAsync(TimeSpan.FromSeconds(5)));
        var timed = await Assert.ThrowsAsync<McpConnectionException>(() => factory.ConnectAsync(config with { ConnectTimeout = TimeSpan.FromMilliseconds(100) }));
        Assert.Equal("McpConnectionTimedOut", timed.Code); Assert.Equal(2, provider.Calls);
        var failed = new Provider(McpTransportKind.StreamableHttp, (_, _) => throw new InvalidOperationException("private-test-secret"));
        var error = await Assert.ThrowsAsync<McpConnectionException>(() => new McpConnectionFactory([failed]).ConnectAsync(config));
        Assert.Equal("McpConnectionFailed", error.Code); Assert.DoesNotContain("private-test-secret", error.Message); Assert.Equal(1, failed.Calls);
    }

    [Fact]
    public async Task StdioConfigurationSnapshotsArgumentsAndSecretReferencesWithoutReadingOrExportingCredentials()
    {
        var arguments = new List<string> { "--synthetic" };
        var environment = new Dictionary<string, string> { ["SCOPED_TEST_KEY"] = "dpapi:" + Guid.NewGuid().ToString("N") };
        McpConnectionConfig? received = null;
        var provider = new Provider(McpTransportKind.Stdio, (config, _) =>
        {
            received = config; arguments.Add("--changed"); environment["SCOPED_TEST_KEY"] = "changed-reference";
            throw new InvalidOperationException("No process in configuration-only test");
        });
        var config = new McpConnectionConfig { Id = Guid.NewGuid(), Name = "Synthetic local server", Transport = McpTransportKind.Stdio,
            Enabled = true, Approved = true, Stdio = new("synthetic.exe", arguments, SecretEnvironment: environment) };
        var expectedReference = environment["SCOPED_TEST_KEY"];
        await Assert.ThrowsAsync<McpConnectionException>(() => new McpConnectionFactory([provider]).ConnectAsync(config));
        Assert.Equal(new[] { "--synthetic" }, received!.Stdio!.Arguments);
        Assert.Equal(expectedReference, received.Stdio.SecretEnvironment!["SCOPED_TEST_KEY"]);
        Assert.Contains("SecretEnvironment", JsonSerializer.Serialize(received));
        Assert.False(new McpConnectionConfig { Id = Guid.NewGuid(), Name = "Default", Transport = McpTransportKind.Stdio }.Approved);
    }

    private static McpConnectionConfig Http(Uri endpoint) => new() { Id = Guid.NewGuid(), Name = "Synthetic HTTP server",
        Transport = McpTransportKind.StreamableHttp, Enabled = true, Approved = true, Http = new(endpoint, AllowLoopbackHttp: endpoint.IsLoopback) };
    private sealed class Provider(McpTransportKind kind, Func<McpConnectionConfig, CancellationToken, ValueTask<IClientTransport>> create) : IMcpTransportProvider
    {
        public McpTransportKind Transport => kind;
        public int Calls;
        public ValueTask<IClientTransport> CreateAsync(McpConnectionConfig configuration, CancellationToken cancellationToken)
        { Calls++; return create(configuration, cancellationToken); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailedHandshakeDisposesTransportAndCleanupErrorsAreSanitized(bool cleanupFails)
    {
        var transport = new FailingTransport(cleanupFails);
        var provider = new Provider(McpTransportKind.StreamableHttp, (_, _) => ValueTask.FromResult<IClientTransport>(transport));
        var error = await Assert.ThrowsAsync<McpConnectionException>(() => new McpConnectionFactory([provider]).ConnectAsync(Http(new Uri("https://example.invalid/mcp"))));
        Assert.Equal(cleanupFails ? "McpCleanupFailed" : "McpConnectionFailed", error.Code);
        Assert.DoesNotContain("private-test-secret", error.Message);
        Assert.Equal(1, transport.Disposals);
    }

    private sealed class FailingTransport(bool cleanupFails) : IClientTransport, IAsyncDisposable
    {
        public string Name => "Synthetic failure";
        public int Disposals;
        public Task<ITransport> ConnectAsync(CancellationToken cancellationToken = default) => throw new InvalidOperationException("private-test-secret handshake");
        public ValueTask DisposeAsync()
        {
            Disposals++;
            if (cleanupFails) throw new InvalidOperationException("private-test-secret cleanup");
            return ValueTask.CompletedTask;
        }
    }
}
