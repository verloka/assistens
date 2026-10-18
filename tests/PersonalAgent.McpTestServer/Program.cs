using ModelContextProtocol.Server;
using System.ComponentModel;

namespace PersonalAgent.McpTestServer;

public static class Program
{
    public static async Task Main(string[] args)
    {
        // This executable is a test fixture only. Stdout belongs to the MCP protocol.
        Console.Error.WriteLine($"MCP_TEST_PID={Environment.ProcessId}");
        var builder = Host.CreateApplicationBuilder(args);
        builder.Logging.ClearProviders();
        builder.Logging.AddConsole(options => options.LogToStandardErrorThreshold = LogLevel.Trace);
        builder.Services.AddMcpServer().WithStdioServerTransport().WithTools<EchoTool>();
        await builder.Build().RunAsync();
    }

    public static WebApplication CreateHttpServer()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = [] });
        builder.Configuration["AllowedHosts"] = "127.0.0.1";
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddMcpServer().WithHttpTransport().WithTools<EchoTool>();
        var app = builder.Build();
        app.MapMcp("/mcp");
        return app;
    }
}

[McpServerToolType]
public sealed class EchoTool
{
    [McpServerTool(Name = "echo"), Description("Echoes a diagnostic message in memory; no external effects.")]
    public static string Echo(string message) => $"Echo: {message}";
}
