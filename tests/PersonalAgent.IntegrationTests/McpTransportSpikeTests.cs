using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using System.Diagnostics;

namespace PersonalAgent.IntegrationTests;

public sealed class McpTransportSpikeTests
{
    [Fact]
    public async Task StdioDiscoversSchemaCallsToolAndClosesChildProcess()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var started = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var transport = new StdioClientTransport(new StdioClientTransportOptions
        {
            Name = "Local diagnostic only",
            Command = "dotnet",
            Arguments = [typeof(McpTestServer.Program).Assembly.Location],
            ShutdownTimeout = TimeSpan.FromSeconds(5),
            StandardErrorLines = line =>
            {
                if (line.StartsWith("MCP_TEST_PID=", StringComparison.Ordinal))
                    started.TrySetResult(int.Parse(line[13..]));
            }
        });
        using var process = await ConnectAndVerifyAsync();
        await process.WaitForExitAsync(timeout.Token);
        Assert.True(process.HasExited);

        async Task<Process> ConnectAndVerifyAsync()
        {
            await using var client = await McpClient.CreateAsync(transport, cancellationToken: timeout.Token);
            var child = Process.GetProcessById(await started.Task.WaitAsync(timeout.Token));
            await VerifyEchoAsync(client, timeout.Token);
            return child;
        }
    }

    [Fact]
    public async Task StreamableHttpDiscoversSchemaAndCallsToolOverLoopback()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var app = McpTestServer.Program.CreateHttpServer();
        await app.StartAsync(timeout.Token);
        try
        {
            var address = Assert.Single(app.Services.GetRequiredService<IServer>()
                .Features.Get<IServerAddressesFeature>()!.Addresses);
            await using var transport = new HttpClientTransport(new HttpClientTransportOptions
            {
                Endpoint = new Uri($"{address}/mcp"),
                TransportMode = HttpTransportMode.StreamableHttp
            });
            await using var client = await McpClient.CreateAsync(transport, cancellationToken: timeout.Token);
            await VerifyEchoAsync(client, timeout.Token);
        }
        finally
        {
            using var shutdown = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await app.StopAsync(shutdown.Token);
        }
    }

    private static async Task VerifyEchoAsync(McpClient client, CancellationToken cancellationToken)
    {
        var tool = Assert.Single(await client.ListToolsAsync(cancellationToken: cancellationToken));
        Assert.Equal("echo", tool.Name);
        Assert.Equal("string", tool.JsonSchema.GetProperty("properties").GetProperty("message").GetProperty("type").GetString());
        var result = await client.CallToolAsync(tool.Name,
            new Dictionary<string, object?> { ["message"] = "local transport test" },
            cancellationToken: cancellationToken);
        Assert.NotEqual(true, result.IsError);
        Assert.Equal("Echo: local transport test", Assert.IsType<TextContentBlock>(Assert.Single(result.Content)).Text);
    }
}
