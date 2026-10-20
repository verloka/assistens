using PersonalAgent.Core.Channels;
using PersonalAgent.Core.Domain;

namespace PersonalAgent.Core.Application;

// Provider-independent boundary. Adapters do not implement task lifecycle or select privileged tools.
public sealed class ChannelMessageRouter
{
    private readonly ITaskCommands commands;
    private readonly IChannelAccessPolicy? policy;
    private readonly IOutboundChannelCatalog? catalog;
    private readonly IReadOnlyDictionary<(string Channel, string Connection), IOutboundChannel> outbound;
    public ChannelMessageRouter(ITaskCommands commands, IEnumerable<IOutboundChannel> outbound, IChannelAccessPolicy? policy = null, IOutboundChannelCatalog? catalog = null)
    {
        this.commands = commands; this.policy = policy; this.catalog = catalog;
        var channels = outbound.ToArray();
        foreach (var channel in channels)
        {
            ChannelFields.Identity(channel.ChannelId, 64); ChannelFields.Identity(channel.ConnectionId, 256);
            ArgumentNullException.ThrowIfNull(channel.Capabilities);
        }
        this.outbound = channels.ToDictionary(value => (value.ChannelId, value.ConnectionId)); // Duplicate registration fails closed.
    }
    public Task RunInboundAsync(IInboundChannel channel, CancellationToken ct)
    {
        ChannelFields.Identity(channel.ChannelId, 64); ChannelFields.Identity(channel.ConnectionId, 256);
        ct.ThrowIfCancellationRequested();
        return channel.RunAsync(async (message, receiveToken) =>
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, receiveToken);
            linked.Token.ThrowIfCancellationRequested();
            var snapshot = message.Snapshot();
            if (snapshot.Route.ChannelId != channel.ChannelId || snapshot.Route.ConnectionId != channel.ConnectionId) throw new ChannelAccessDeniedException();
            await SubmitAsync(snapshot, linked.Token).ConfigureAwait(false);
        }, ct);
    }
    public async Task<TaskRun> SubmitAsync(IncomingMessage message, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested(); var snapshot = message.Snapshot();
        if (policy is null || !await policy.AllowInboundAsync(snapshot, ct).ConfigureAwait(false)) throw new ChannelAccessDeniedException();
        ct.ThrowIfCancellationRequested();
        if (snapshot.ActionToken is not null || snapshot.Attachments.Count > 0)
            throw new NotSupportedException("This task receiver accepts plain text only; action and attachment handling requires an explicit adapter flow.");
        var route = snapshot.Route;
        var source = "channel/" + Uri.EscapeDataString(route.ChannelId) + "/" + Uri.EscapeDataString(route.ConnectionId) + "/" + Uri.EscapeDataString(route.DestinationId);
        return await commands.SubmitAsync(new(Guid.NewGuid(), snapshot.Text, new("channel/" + route.ChannelId, snapshot.SenderId, snapshot.ExternalMessageId),
            ExternalKey: new(source, snapshot.ExternalMessageId)), ct).ConfigureAwait(false);
    }
    public async Task SendAsync(OutgoingMessage message, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested(); var snapshot = message.Snapshot();
        if (policy is null || !await policy.AllowOutboundAsync(snapshot, ct).ConfigureAwait(false)) throw new ChannelAccessDeniedException();
        if (!outbound.TryGetValue((snapshot.Destination.ChannelId, snapshot.Destination.ConnectionId), out var channel))
            channel = catalog is null ? null : await catalog.FindAsync(snapshot.Destination, ct).ConfigureAwait(false);
        if (channel is null || channel.ChannelId != snapshot.Destination.ChannelId || channel.ConnectionId != snapshot.Destination.ConnectionId) throw new ChannelAccessDeniedException();
        if (!channel.Capabilities.SupportsActions && snapshot.Actions.Count > 0)
        {
            snapshot = snapshot with { Text = snapshot.Text + "\n\nOpen Personal Agent to review: " + string.Join(", ", snapshot.Actions.Select(action => action.Label)), Actions = [] };
            snapshot = snapshot.Snapshot();
        }
        ct.ThrowIfCancellationRequested();
        await channel.SendAsync(snapshot, ct).ConfigureAwait(false); // No retry or run-state mutation; durable outbox is a separate boundary.
    }
}
