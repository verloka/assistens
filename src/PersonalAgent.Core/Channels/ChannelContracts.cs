using PersonalAgent.Core.Domain;
using System.Text.Json.Serialization;

namespace PersonalAgent.Core.Channels;

public sealed record ChannelRoute(string ChannelId, string ConnectionId, string DestinationId)
{
    public void Validate()
    {
        ChannelFields.Identity(ChannelId, 64); ChannelFields.Identity(ConnectionId, 256); ChannelFields.Identity(DestinationId, 256);
    }
}
[JsonConverter(typeof(JsonStringEnumConverter<ChannelActionKind>))]
public enum ChannelActionKind { Approve, Reject, Cancel, OpenDetails, Pause }
// Tokens identify a future exact action; the kind/label in a message never grants authority.
public sealed record ChannelMessageAction(ChannelActionKind Kind, string Label, string Token);
public sealed record ChannelAttachment(string ExternalId, string ContentType, string Name, long? SizeBytes = null);
public sealed record ChannelCapabilities(bool SupportsActions = false);

public sealed record IncomingMessage
{
    public int SchemaVersion { get; init; } = 1;
    public required ChannelRoute Route { get; init; }
    public required string SenderId { get; init; }
    public required string ExternalMessageId { get; init; }
    public required DateTimeOffset TimestampUtc { get; init; }
    public string Text { get; init; } = "";
    public string? CorrelationKey { get; init; }
    public string? ActionToken { get; init; }
    public IReadOnlyList<ChannelAttachment> Attachments { get; init; } = [];

    public IncomingMessage Snapshot()
    {
        if (SchemaVersion != 1) throw new NotSupportedException("Unsupported incoming channel message version.");
        ArgumentNullException.ThrowIfNull(Route); Route.Validate();
        ChannelFields.Identity(SenderId, 256); ChannelFields.Identity(ExternalMessageId, 256);
        ChannelFields.Text(Text, 16_384); ChannelFields.OptionalIdentity(CorrelationKey, 512); ChannelFields.OptionalIdentity(ActionToken, 512);
        if (TimestampUtc.UtcTicks == 0 || Attachments is null || Attachments.Count > 8) throw new ArgumentException("Invalid incoming message metadata.");
        var attachments = Attachments.ToArray();
        foreach (var item in attachments)
        {
            if (item is null || item.SizeBytes < 0) throw new ArgumentException("Invalid attachment metadata.");
            ChannelFields.Identity(item.ExternalId, 512); ChannelFields.Identity(item.ContentType, 128); ChannelFields.Identity(item.Name, 256);
        }
        if (string.IsNullOrWhiteSpace(Text) && ActionToken is null && attachments.Length == 0) throw new ArgumentException("Message content is required.");
        return this with { TimestampUtc = TimestampUtc.ToUniversalTime(), Attachments = Array.AsReadOnly(attachments) };
    }
}

public sealed record OutgoingMessage
{
    public int SchemaVersion { get; init; } = 1;
    public required Guid Id { get; init; }
    public required ChannelRoute Destination { get; init; }
    public required string Text { get; init; }
    public Guid? RunId { get; init; }
    public Guid? ConversationId { get; init; }
    public IReadOnlyList<ChannelMessageAction> Actions { get; init; } = [];

    public OutgoingMessage Snapshot()
    {
        if (SchemaVersion != 1) throw new NotSupportedException("Unsupported outgoing channel message version.");
        ArgumentNullException.ThrowIfNull(Destination); Destination.Validate(); ChannelFields.Text(Text, 65_536);
        if (Id == Guid.Empty || RunId == Guid.Empty || ConversationId == Guid.Empty || string.IsNullOrWhiteSpace(Text)
            || Actions is null || Actions.Count > 16) throw new ArgumentException("Invalid outgoing message metadata.");
        var actions = Actions.ToArray();
        foreach (var action in actions)
        {
            if (action is null || !Enum.IsDefined(action.Kind)) throw new ArgumentException("Invalid message action.");
            ChannelFields.Identity(action.Label, 128); ChannelFields.Identity(action.Token, 512);
        }
        if (actions.Length > 0 && (RunId is null || actions.Select(value => value.Token).Distinct(StringComparer.Ordinal).Count() != actions.Length))
            throw new ArgumentException("Actions require a related run and distinct exact tokens.");
        return this with { Actions = Array.AsReadOnly(actions) };
    }
}

public interface IInboundChannel
{
    string ChannelId { get; }
    string ConnectionId { get; }
    // Await durable acceptance before acknowledging an external message/advancing a receive offset.
    Task RunAsync(Func<IncomingMessage, CancellationToken, Task> accept, CancellationToken ct);
}
public interface IOutboundChannel
{
    string ChannelId { get; }
    string ConnectionId { get; }
    ChannelCapabilities Capabilities { get; }
    Task SendAsync(OutgoingMessage message, CancellationToken ct);
}
public interface IChannelAccessPolicy
{
    ValueTask<bool> AllowInboundAsync(IncomingMessage message, CancellationToken ct);
    ValueTask<bool> AllowOutboundAsync(OutgoingMessage message, CancellationToken ct);
}
public sealed class ChannelAccessDeniedException() : InvalidOperationException("Channel sender or destination is not authorized.");

internal static class ChannelFields
{
    public static void Identity(string value, int max)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > max || value.Any(char.IsControl)) throw new ArgumentException("Invalid channel identity or metadata.");
    }
    public static void OptionalIdentity(string? value, int max) { if (value is not null) Identity(value, max); }
    public static void Text(string value, int max) { if (value is null || value.Length > max) throw new ArgumentException("Invalid channel text."); }
}
