namespace PersonalAgent.Persistence.Entities;

public sealed class ChannelConversationLinkRow
{
    public required string OwnerSource { get; set; }
    public required string OwnerId { get; set; }
    public required string ChannelId { get; set; }
    public required string ConnectionId { get; set; }
    public required string DestinationId { get; set; }
    public required string SenderId { get; set; }
    public required string ExternalMessageId { get; set; }
    public Guid ConversationId { get; set; }
    public Guid RunId { get; set; }
    public string? ExternalUpdateId { get; set; }
    public Guid? OutgoingMessageId { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
    public int PayloadSchemaVersion { get; set; } = 1;
}
