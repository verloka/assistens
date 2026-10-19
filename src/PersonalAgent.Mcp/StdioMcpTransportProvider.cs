using Microsoft.Extensions.Logging;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using PersonalAgent.Core.Agents;
using PersonalAgent.Core.Security;
using System.Diagnostics;
using System.Text.RegularExpressions;

namespace PersonalAgent.Mcp;

public sealed class StdioMcpTransportProvider(ISecretStore secrets, ILogger<StdioMcpTransportProvider> logger) : IMcpTransportProvider
{
    public McpTransportKind Transport => McpTransportKind.Stdio;

    public async ValueTask<IClientTransport> CreateAsync(McpConnectionConfig configuration, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!configuration.Enabled || !configuration.Approved || configuration.Transport != Transport || configuration.Stdio is not { } config)
            throw Error("McpConnectionNotApproved", "The configured stdio command has not been approved by the owner.");
        if (!Path.IsPathFullyQualified(config.Command) || !File.Exists(config.Command)
            || !string.Equals(Path.GetExtension(config.Command), ".exe", StringComparison.OrdinalIgnoreCase)
            || config.Arguments is null || config.Arguments.Any(argument => argument is null || argument.Contains('\0')))
            throw Error("McpCommandInvalid", "Select an explicit existing executable and argument list for the MCP server.");
        var directory = config.WorkingDirectory ?? Path.GetDirectoryName(config.Command)!;
        if (!Path.IsPathFullyQualified(directory) || !Directory.Exists(directory))
            throw Error("McpCommandInvalid", "The configured MCP working directory is unavailable.");
        var bindings = config.SecretEnvironment?.ToArray() ?? [];
        if (bindings.Select(binding => binding.Key).Distinct(StringComparer.OrdinalIgnoreCase).Count() != bindings.Length
            || bindings.Any(binding => !Regex.IsMatch(binding.Key, "^[A-Z_][A-Z0-9_]{0,127}$") || string.IsNullOrWhiteSpace(binding.Value)
                || ReservedEnvironment.Contains(binding.Key)))
            throw Error("McpCredentialScopeInvalid", "MCP credential bindings must use distinct non-system environment names.");
        var command = Path.GetFullPath(config.Command);
        var arguments = config.Arguments.ToArray();
        var environment = StdioClientTransportOptions.GetDefaultEnvironmentVariables();
        try
        {
            foreach (var binding in bindings)
            {
                var secret = await secrets.ReadAsync(binding.Value, cancellationToken).ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(secret)) throw Error("McpSecretUnavailable", "A configured MCP credential is unavailable.");
                environment[binding.Key] = secret;
            }
            cancellationToken.ThrowIfCancellationRequested();
            return new ProcessTransport(configuration.Id, command, arguments, directory, environment, logger);
        }
        catch (OperationCanceledException) { throw; }
        catch (McpConnectionException) { throw; }
        catch (Exception) { throw Error("McpSecretUnavailable", "The configured MCP credential store is unavailable."); }
    }

    private static readonly HashSet<string> ReservedEnvironment = new(StringComparer.OrdinalIgnoreCase)
    { "PATH", "PATHEXT", "COMSPEC", "SYSTEMROOT", "WINDIR", "DOTNET_ROOT", "DOTNET_STARTUP_HOOKS", "DOTNET_ADDITIONAL_DEPS", "DOTNET_SHARED_STORE", "COR_ENABLE_PROFILING", "COR_PROFILER", "CORECLR_ENABLE_PROFILING", "CORECLR_PROFILER" };
    private static McpConnectionException Error(string code, string message) => new(code, message);

    // The pinned SDK's Windows StdioClientTransport inserts cmd.exe. Use its stream protocol over direct argv instead.
    private sealed class ProcessTransport(Guid id, string command, string[] arguments, string directory,
        IDictionary<string, string?> environment, ILogger logger) : IClientTransport, IAsyncDisposable
    {
        private Process? process;
        private Task? stderr;
        private int connected, disposed;
        private long stderrCharacters;
        private bool started;
        public string Name => "mcp-" + id.ToString("N");

        public async Task<ITransport> ConnectAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
            if (Interlocked.Exchange(ref connected, 1) != 0) throw Error("McpAlreadyConnected", "This MCP transport already owns a process.");
            var start = new ProcessStartInfo(command)
            {
                WorkingDirectory = directory, UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true
            };
            foreach (var argument in arguments) start.ArgumentList.Add(argument);
            start.Environment.Clear();
            foreach (var variable in environment) start.Environment[variable.Key] = variable.Value;
            process = new Process { StartInfo = start };
            if (!process.Start()) throw Error("McpProcessStartFailed", "The MCP process could not be started.");
            started = true;
            start.Environment.Clear();
            environment.Clear();
            logger.LogInformation(new EventId(4100), "MCP connection {ConnectionId} started process {ProcessId}.", id, process.Id);
            stderr = DrainStderrAsync(process.StandardError);
            cancellationToken.ThrowIfCancellationRequested();
            return await new StreamClientTransport(process.StandardInput.BaseStream, process.StandardOutput.BaseStream)
                .ConnectAsync(cancellationToken).ConfigureAwait(false);
        }

        private async Task DrainStderrAsync(StreamReader reader)
        {
            var buffer = new char[1024];
            try
            {
                int count;
                while ((count = await reader.ReadAsync(buffer.AsMemory()).ConfigureAwait(false)) != 0)
                { Interlocked.Add(ref stderrCharacters, count); Array.Clear(buffer); }
            }
            catch (Exception exception) when (exception is IOException or ObjectDisposedException) { }
            finally { Array.Clear(buffer); }
        }

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref disposed, 1) != 0) return;
            environment.Clear();
            if (process is null) return;
            process.StartInfo.Environment.Clear();
            if (!started) { process.Dispose(); return; }
            try
            {
                try { process.StandardInput.Close(); } catch (Exception exception) when (exception is InvalidOperationException or IOException or ObjectDisposedException) { }
                if (!process.HasExited)
                {
                    try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false); }
                    catch (TimeoutException)
                    {
                        if (!process.HasExited) process.Kill(entireProcessTree: true);
                        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false);
                    }
                }
                if (stderr is not null) await stderr.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
                logger.LogInformation(new EventId(4101), "MCP connection {ConnectionId} process exited; {StderrCharacters} stderr characters suppressed.", id, Interlocked.Read(ref stderrCharacters));
            }
            finally { process.Dispose(); }
        }
    }
}
