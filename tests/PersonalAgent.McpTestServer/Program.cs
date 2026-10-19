using ModelContextProtocol.Server;
using System.ComponentModel;
using ModelContextProtocol.Protocol;

namespace PersonalAgent.McpTestServer;

public static class Program
{
    public static async Task Main(string[] args)
    {
        // This executable is a test fixture only. Stdout belongs to the MCP protocol.
        Console.Error.WriteLine($"MCP_TEST_PID={Environment.ProcessId}");
        if (args.Contains("--fixture-no-protocol", StringComparer.Ordinal))
        {
            await Task.Delay(Timeout.InfiniteTimeSpan);
            return;
        }
        var fixtureCredential = Environment.GetEnvironmentVariable("MCP_FIXTURE_SECRET");
        if (fixtureCredential is not null)
        {
            Console.Error.WriteLine("Synthetic stderr credential: " + fixtureCredential);
            Console.Error.WriteLine("Synthetic argv: " + string.Join(";", args));
        }
        var builder = Host.CreateApplicationBuilder(args);
        builder.Logging.ClearProviders();
        builder.Logging.AddConsole(options => options.LogToStandardErrorThreshold = LogLevel.Trace);
        builder.Services.AddMcpServer().WithStdioServerTransport().WithTools<EchoTool>();
        await builder.Build().RunAsync();
    }

    public static WebApplication CreateHttpServer(Func<HttpContext, Task>? observe = null,
        Func<IList<ModelContextProtocol.Protocol.Tool>>? listTools = null,
        McpRequestHandler<CallToolRequestParams, CallToolResult>? callTool = null)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = [] });
        builder.Configuration["AllowedHosts"] = "127.0.0.1";
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        var mcp = builder.Services.AddMcpServer().WithHttpTransport();
        if (listTools is null) mcp.WithTools<EchoTool>();
        else mcp.WithListToolsHandler((_, _) => ValueTask.FromResult(new ModelContextProtocol.Protocol.ListToolsResult { Tools = listTools() }));
        if (callTool is not null) mcp.WithCallToolHandler(callTool);
        var app = builder.Build();
        if (observe is not null) app.Use(async (context, next) => { await observe(context); await next(context); });
        app.MapMcp("/mcp");
        return app;
    }
}

[McpServerToolType]
public sealed class EchoTool
{
    [McpServerTool(Name = "echo"), Description("Echoes a diagnostic message in memory; no external effects.")]
    public static async Task<string> Echo(string message, CancellationToken cancellationToken)
    {
        if (message == "__fixture_wait__") await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        return message == "__fixture_environment__" ? System.Text.Json.JsonSerializer.Serialize(new
        {
            AmbientPresent = Environment.GetEnvironmentVariable("PERSONAL_AGENT_TEST_AMBIENT_SECRET") is not null,
            ScopedPresent = Environment.GetEnvironmentVariable("MCP_FIXTURE_SECRET") is not null,
            Arguments = Environment.GetCommandLineArgs().Skip(1).ToArray()
        }) : $"Echo: {message}";
    }
}
