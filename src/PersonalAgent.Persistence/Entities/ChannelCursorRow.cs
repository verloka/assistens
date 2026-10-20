namespace PersonalAgent.Persistence.Entities;

public sealed class ChannelCursorRow
{
    public required string ChannelId { get; set; }
    public required string ConnectionId { get; set; }
    public long NextSequence { get; set; }
    public long Version { get; set; }
    public int PayloadSchemaVersion { get; set; } = 1;
}
