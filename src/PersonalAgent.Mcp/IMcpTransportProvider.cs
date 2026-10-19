using ModelContextProtocol.Client;
using PersonalAgent.Core.Agents;

namespace PersonalAgent.Mcp;

// SDK types stay in the MCP adapter; concrete transports are registered through DI.
public interface IMcpTransportProvider
{
    McpTransportKind Transport { get; }
    ValueTask<IClientTransport> CreateAsync(McpConnectionConfig configuration, CancellationToken cancellationToken);
}

public sealed class McpConnectionException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}
