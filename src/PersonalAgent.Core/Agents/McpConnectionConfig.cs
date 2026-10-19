namespace PersonalAgent.Core.Agents;

public enum McpTransportKind { Stdio, StreamableHttp }

// Configuration comes from the owner, never from model/tool output. Only opaque secret references are persisted.
public sealed record McpStdioConfig(string Command, IReadOnlyList<string> Arguments, string? WorkingDirectory = null,
    IReadOnlyDictionary<string, string>? SecretEnvironment = null);
public sealed record McpHttpConfig(Uri Endpoint, string? BearerSecretRef = null, bool AllowLoopbackHttp = false);
public sealed record McpConnectionConfig
{
    public int SchemaVersion { get; init; } = 1;
    public required Guid Id { get; init; }
    public required string Name { get; init; }
    public required McpTransportKind Transport { get; init; }
    public bool Enabled { get; init; }
    public bool Approved { get; init; }
    public McpStdioConfig? Stdio { get; init; }
    public McpHttpConfig? Http { get; init; }
    public IReadOnlyList<string> AllowedTools { get; init; } = [];
    public TimeSpan ConnectTimeout { get; init; } = TimeSpan.FromSeconds(15);
}
