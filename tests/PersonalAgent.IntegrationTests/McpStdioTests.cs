using Microsoft.Extensions.Logging;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using PersonalAgent.Core.Agents;
using PersonalAgent.Core.Security;
using PersonalAgent.Mcp;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace PersonalAgent.IntegrationTests;

public sealed class McpStdioTests
{
    private const string SyntheticSecret = "synthetic-local-mcp-key-do-not-log";
    private static string Dotnet => Path.GetFullPath(Path.Combine(RuntimeEnvironment.GetRuntimeDirectory(), "..", "..", "..", "dotnet.exe"));
    private static McpConnectionConfig Config(params string[] args) => new()
    {
        Id = Guid.NewGuid(), Name = "Synthetic stdio server", Transport = McpTransportKind.Stdio, Enabled = true, Approved = true,
        Stdio = new(Dotnet, [typeof(McpTestServer.Program).Assembly.Location, ..args]), AllowedTools = ["echo"]
    };

    [Fact]
    public async Task DirectApprovedArgvPreservesLiteralShellCharactersScopesSecretAndClosesActualChild()
    {
        const string argument = "--fixture-argument=spaces & | < > ^ % ! \"quotes\"";
        var logger = new RecordingLog(); var secrets = new Secrets();
        var factory = new McpConnectionFactory([new StdioMcpTransportProvider(secrets, logger)]);
        var previous = Environment.GetEnvironmentVariable("PERSONAL_AGENT_TEST_AMBIENT_SECRET");
        Environment.SetEnvironmentVariable("PERSONAL_AGENT_TEST_AMBIENT_SECRET", "synthetic-unrelated-key");
        try
        {
            var config = Config(argument);
            config = config with { Stdio = config.Stdio! with { SecretEnvironment = new Dictionary<string, string> { ["MCP_FIXTURE_SECRET"] = "opaque-scoped-ref" } } };
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            await using (var connection = await factory.ConnectAsync(config, timeout.Token))
            {
                using var child = Process.GetProcessById(await logger.Started.Task.WaitAsync(timeout.Token));
                Assert.False(child.HasExited);
                Assert.Equal("echo", Assert.Single(await connection.Client.ListToolsAsync(cancellationToken: timeout.Token)).Name);
                var reply = await connection.Client.CallToolAsync("echo", new Dictionary<string, object?> { ["message"] = "__fixture_environment__" }, cancellationToken: timeout.Token);
                using var body = JsonDocument.Parse(Assert.IsType<TextContentBlock>(Assert.Single(reply.Content)).Text);
                Assert.False(body.RootElement.GetProperty("AmbientPresent").GetBoolean());
                Assert.True(body.RootElement.GetProperty("ScopedPresent").GetBoolean());
                Assert.Contains(argument, body.RootElement.GetProperty("Arguments").EnumerateArray().Select(value => value.GetString()));
                await connection.DisposeAsync();
                await child.WaitForExitAsync(timeout.Token);
                Assert.True(child.HasExited);
            }
            Assert.Equal(1, secrets.Reads);
            Assert.Equal("opaque-scoped-ref", Assert.Single(secrets.References));
            Assert.DoesNotContain(SyntheticSecret, string.Join("\n", logger.Messages));
            Assert.DoesNotContain("synthetic-unrelated-key", string.Join("\n", logger.Messages));
            Assert.Contains(logger.Messages, message => message.Contains("stderr characters suppressed"));
        }
        finally { Environment.SetEnvironmentVariable("PERSONAL_AGENT_TEST_AMBIENT_SECRET", previous); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancelledOrTimedOutHandshakeTerminatesActualChildWithoutRetry(bool cancel)
    {
        var logger = new RecordingLog();
        var factory = new McpConnectionFactory([new StdioMcpTransportProvider(new Secrets(), logger)]);
        var config = Config("--fixture-no-protocol") with { ConnectTimeout = TimeSpan.FromSeconds(cancel ? 15 : 1) };
        using var stop = new CancellationTokenSource();
        var connecting = factory.ConnectAsync(config, stop.Token);
        using var child = Process.GetProcessById(await logger.Started.Task.WaitAsync(TimeSpan.FromSeconds(5)));
        if (cancel)
        { stop.Cancel(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => connecting.WaitAsync(TimeSpan.FromSeconds(10))); }
        else
        {
            var error = await Assert.ThrowsAsync<McpConnectionException>(() => connecting.WaitAsync(TimeSpan.FromSeconds(10)));
            Assert.Equal("McpConnectionTimedOut", error.Code);
        }
        await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(child.HasExited);
        Assert.Single(logger.Messages, message => message.Contains("started process"));
    }

    [Theory]
    [InlineData("approval", "McpConnectionNotApproved")]
    [InlineData("relative-command", "McpCommandInvalid")]
    [InlineData("working-directory", "McpCommandInvalid")]
    [InlineData("reserved-environment", "McpCredentialScopeInvalid")]
    [InlineData("missing-secret", "McpSecretUnavailable")]
    public async Task InvalidCommandScopeOrMissingSecretNeverStartsProcess(string invalid, string code)
    {
        var logger = new RecordingLog(); var secrets = new Secrets(invalid == "missing-secret" ? null : SyntheticSecret);
        var config = Config();
        config = invalid switch
        {
            "approval" => config with { Approved = false },
            "relative-command" => config with { Stdio = config.Stdio! with { Command = "dotnet.exe" } },
            "working-directory" => config with { Stdio = config.Stdio! with { WorkingDirectory = ".." } },
            _ => config with { Stdio = config.Stdio! with { SecretEnvironment = new Dictionary<string, string> { [invalid == "reserved-environment" ? "PATH" : "MCP_FIXTURE_SECRET"] = "scoped-ref" } } }
        };
        var error = await Assert.ThrowsAsync<McpConnectionException>(() => new McpConnectionFactory([new StdioMcpTransportProvider(secrets, logger)]).ConnectAsync(config));
        Assert.Equal(code, error.Code);
        Assert.False(logger.Started.Task.IsCompleted);
        Assert.DoesNotContain(SyntheticSecret, error.Message);
        if (invalid != "missing-secret") Assert.Equal(0, secrets.Reads);
    }

    private sealed class Secrets(string? value = SyntheticSecret) : ISecretStore
    {
        public int Reads;
        public List<string> References { get; } = [];
        public ValueTask<string> StoreAsync(string secret, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask<string?> ReadAsync(string reference, CancellationToken cancellationToken = default)
        { cancellationToken.ThrowIfCancellationRequested(); Reads++; References.Add(reference); return ValueTask.FromResult(value); }
    }
    private sealed class RecordingLog : ILogger<StdioMcpTransportProvider>
    {
        public ConcurrentQueue<string> Messages { get; } = new();
        public TaskCompletionSource<int> Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            Messages.Enqueue(formatter(state, exception));
            if (eventId.Id == 4100 && state is IEnumerable<KeyValuePair<string, object?>> values)
                Started.TrySetResult((int)values.Single(value => value.Key == "ProcessId").Value!);
        }
    }
}
