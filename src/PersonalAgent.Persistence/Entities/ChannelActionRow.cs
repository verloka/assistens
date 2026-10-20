namespace PersonalAgent.Persistence.Entities;

public sealed class ChannelActionRow
{
    public required string TokenHash { get; set; }
    public required string ScopeJson { get; set; }
    public int PayloadSchemaVersion { get; set; } = 1;
}
