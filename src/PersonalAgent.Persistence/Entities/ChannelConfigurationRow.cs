namespace PersonalAgent.Persistence.Entities;

public sealed class ChannelConfigurationRow
{
    public Guid Id { get; set; }
    public required string OwnerSource { get; set; }
    public required string OwnerId { get; set; }
    public required string ChannelId { get; set; }
    public required string ConnectionId { get; set; }
    public required string DestinationId { get; set; }
    public bool Enabled { get; set; }
    public string? RemoteIdentity { get; set; }
    public long Version { get; set; }
    public int PayloadSchemaVersion { get; set; } = 1;
    public required string ConfigurationJson { get; set; }
}
