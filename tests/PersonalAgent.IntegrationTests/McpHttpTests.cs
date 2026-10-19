using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Client;
using PersonalAgent.Core.Agents;
using PersonalAgent.Core.Security;
using PersonalAgent.Mcp;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.Json;

namespace PersonalAgent.IntegrationTests;

public sealed class McpHttpTests
{
    private const string SyntheticKey = "synthetic-http-mcp-token-do-not-log";

    [Fact]
    public async Task ActualHttpServerReceivesScopedAuthListsCallsAndReconnectsWithFreshSessionWithoutReplay()
    {
        var initialize = 0; var calls = 0; var authFailures = 0;
        await using var server = McpTestServer.Program.CreateHttpServer(async context =>
        {
            if (context.Request.Headers.Authorization != "Bearer " + SyntheticKey) Interlocked.Increment(ref authFailures);
            var method = await Method(context);
            if (method is "initialize" or "server/discover") Interlocked.Increment(ref initialize);
            if (method == "tools/call") Interlocked.Increment(ref calls);
        });
        await server.StartAsync();
        try
        {
            var config = Config(Endpoint(server.Services));
            var store = new Secrets(); var factory = new McpConnectionFactory([new HttpMcpTransportProvider(store)]);
            string? session;
            McpClient previousClient;
            await using (var connection = await factory.ConnectAsync(config))
            {
                session = connection.Client.SessionId;
                previousClient = connection.Client;
                Assert.Equal("echo", Assert.Single(await connection.ListToolsAsync()).Name);
                var reply = await connection.CallToolAsync("echo", new Dictionary<string, object?> { ["message"] = "remote transport fixture" });
                Assert.NotEqual(true, reply.IsError);
                Assert.Equal("Echo: remote transport fixture", Assert.IsType<TextContentBlock>(Assert.Single(reply.Content)).Text);
            }
            await using (var reconnected = await factory.ConnectAsync(config))
            {
                Assert.NotSame(previousClient, reconnected.Client);
                if (session is not null) Assert.NotEqual(session, reconnected.Client.SessionId);
                Assert.Equal("echo", Assert.Single(await reconnected.ListToolsAsync()).Name);
            }
            Assert.Equal(2, initialize); Assert.Equal(1, calls); Assert.Equal(0, authFailures);
            Assert.Equal(2, store.Reads);
            Assert.DoesNotContain(SyntheticKey, JsonSerializer.Serialize(config));
        }
        finally { await server.StopAsync(); }
    }

    [Theory]
    [InlineData(302, "McpRedirectDenied")]
    [InlineData(401, "McpAuthenticationFailed")]
    [InlineData(403, "McpAuthenticationFailed")]
    [InlineData(404, "McpEndpointUnavailable")]
    [InlineData(429, "McpRateLimited")]
    [InlineData(500, "McpServerUnavailable")]
    public async Task HttpStatusIsStructuredSanitizedAndNeverRedirectedOrRetried(int status, string code)
    {
        var handler = new StatusHandler(status);
        var factory = new McpConnectionFactory([new HttpMcpTransportProvider(new Secrets(), () => handler)]);
        var error = await Assert.ThrowsAsync<McpConnectionException>(() => factory.ConnectAsync(Config(new Uri("https://example.invalid/mcp"))));
        Assert.Equal(code, error.Code);
        Assert.Equal(1, handler.Calls); Assert.True(handler.SawScopedAuth); Assert.True(handler.Disposed);
        Assert.DoesNotContain(SyntheticKey, error.Message);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RequestTimeoutOrCancellationAbortsActualToolWithoutReplayingIt(bool cancel)
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        await using var server = McpTestServer.Program.CreateHttpServer(async context =>
        { if (await Method(context) == "tools/call") { Interlocked.Increment(ref calls); entered.TrySetResult(); } });
        await server.StartAsync();
        try
        {
            var factory = new McpConnectionFactory([new HttpMcpTransportProvider(new Secrets())]);
            await using var connection = await factory.ConnectAsync(Config(Endpoint(server.Services)) with { RequestTimeout = TimeSpan.FromSeconds(cancel ? 10 : 1) });
            using var stop = new CancellationTokenSource();
            var calling = connection.CallToolAsync("echo", new Dictionary<string, object?> { ["message"] = "__fixture_wait__" }, stop.Token);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            if (cancel)
            { stop.Cancel(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => calling.WaitAsync(TimeSpan.FromSeconds(5))); }
            else
            {
                var error = await Assert.ThrowsAsync<McpConnectionException>(() => calling.WaitAsync(TimeSpan.FromSeconds(5)));
                Assert.Equal("McpRequestTimedOut", error.Code);
            }
            Assert.Equal(1, calls);
        }
        finally { await server.StopAsync(); }
    }

    [Fact]
    public async Task MissingCredentialOrUnapprovedHttpScopeStopsBeforeNetwork()
    {
        var handler = new StatusHandler(200);
        var store = new Secrets(null);
        var factory = new McpConnectionFactory([new HttpMcpTransportProvider(store, () => handler)]);
        var error = await Assert.ThrowsAsync<McpConnectionException>(() => factory.ConnectAsync(Config(new Uri("https://example.invalid/mcp"))));
        Assert.Equal("McpSecretUnavailable", error.Code); Assert.Equal(0, handler.Calls);
        var config = Config(new Uri("http://127.0.0.1:12345/mcp"));
        error = await Assert.ThrowsAsync<McpConnectionException>(() => factory.ConnectAsync(config with { Http = config.Http! with { AllowLoopbackHttp = false } }));
        Assert.Equal("McpConfigurationInvalid", error.Code); Assert.Equal(1, store.Reads); Assert.Equal(0, handler.Calls);
    }

    private static McpConnectionConfig Config(Uri endpoint) => new() { Id = Guid.NewGuid(), Name = "Synthetic HTTP MCP",
        Enabled = true, Approved = true, Transport = McpTransportKind.StreamableHttp,
        Http = new(endpoint, "opaque-scoped-ref", AllowLoopbackHttp: endpoint.IsLoopback) };
    private static Uri Endpoint(IServiceProvider services) => new(Assert.Single(services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses) + "/mcp");
    private static async Task<string?> Method(HttpContext context)
    {
        if (context.Request.Method != "POST") return null;
        context.Request.EnableBuffering();
        using var body = await JsonDocument.ParseAsync(context.Request.Body);
        context.Request.Body.Position = 0;
        return body.RootElement.TryGetProperty("method", out var method) ? method.GetString() : null;
    }
    private sealed class Secrets(string? value = SyntheticKey) : ISecretStore
    {
        public int Reads;
        public ValueTask<string> StoreAsync(string secret, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask<string?> ReadAsync(string reference, CancellationToken cancellationToken = default)
        { cancellationToken.ThrowIfCancellationRequested(); Assert.Equal("opaque-scoped-ref", reference); Reads++; return ValueTask.FromResult(value); }
    }
    private sealed class StatusHandler(int status) : HttpMessageHandler
    {
        public int Calls; public bool SawScopedAuth, Disposed;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++; SawScopedAuth = request.Headers.Authorization?.Parameter == SyntheticKey;
            Assert.Equal("https://example.invalid/mcp", request.RequestUri!.AbsoluteUri);
            var response = new HttpResponseMessage((HttpStatusCode)status) { Content = new StringContent(SyntheticKey) };
            if (status == 302) response.Headers.Location = new Uri("https://unapproved.invalid/mcp");
            return Task.FromResult(response);
        }
        protected override void Dispose(bool disposing) { Disposed = true; base.Dispose(disposing); }
    }
}
