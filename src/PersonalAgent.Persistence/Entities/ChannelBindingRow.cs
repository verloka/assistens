using PersonalAgent.Core.Channels;

namespace PersonalAgent.Persistence.Entities;

public sealed class ChannelBindingRow
{
    public Guid Id { get; set; }
    public int PayloadSchemaVersion { get; set; } = 1;
    public string ChannelId { get; set; } = "";
    public string ConnectionId { get; set; } = "";
    public string DestinationId { get; set; } = "";
    public string SenderId { get; set; } = "";
    public string OwnerSource { get; set; } = "";
    public string OwnerId { get; set; } = "";
    public ChannelBindingStatus Status { get; set; }
    public long Version { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset UpdatedAtUtc { get; set; }
}
