namespace PersonalAgent.Core.Agents;

public sealed record McpConnectionProfile
{
    public int SchemaVersion { get; init; } = 1;
    public required McpConnectionConfig Configuration { get; init; }
    public IReadOnlyList<McpToolPermission> Permissions { get; init; } = [];
}
