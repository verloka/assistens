namespace PersonalAgent.Persistence.Entities;

public sealed class McpConnectionRow
{
    public Guid Id { get; set; }
    public string ProfileJson { get; set; } = "";
}
