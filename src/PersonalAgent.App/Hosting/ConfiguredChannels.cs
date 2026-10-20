using System.Collections.Concurrent;
using PersonalAgent.Channels.Telegram;
using PersonalAgent.Core.Application;
using PersonalAgent.Core.Channels;
using PersonalAgent.Core.Persistence;
using PersonalAgent.Core.Security;

namespace PersonalAgent.App;

public sealed class ConfiguredChannelPolicy(IChannelConfigurationRepository configurations, ApprovalOwner owner, Guid id) : IChannelConnectionPolicy
{
    private async Task<ChannelConfiguration?> Current(CancellationToken ct) => (await configurations.ListAsync(owner, ct)).SingleOrDefault(item => item.Id == id && item.Enabled && item.PeerAuthorized);
    public async ValueTask<bool> AllowInboundAsync(IncomingMessage message, CancellationToken ct) =>
        await Current(ct) is { ReceiveTasks: true } current && current.Route == message.Route && current.SenderId == message.SenderId;
    public async ValueTask<bool> AllowOutboundAsync(OutgoingMessage message, CancellationToken ct) =>
        await Current(ct) is { } current && current.Route == message.Destination;
}

public sealed class ConfiguredNotificationSubscriptions(IChannelConfigurationRepository configurations, ApprovalOwner owner) : INotificationSubscriptionSource
{
    public async Task<IReadOnlyList<NotificationSubscription>> ListAsync(CancellationToken ct = default) =>
        (await configurations.ListAsync(owner, ct)).Where(item => item.Enabled && item.PeerAuthorized && item.Events.Count > 0)
            .Select(item => new NotificationSubscription(item.Route, item.Events, item.SinceUtc).Snapshot()).ToArray();
}

public sealed class ConfiguredChannelCatalog(IChannelConfigurationRepository configurations, ApprovalOwner owner, IChannelBindingRepository bindings,
    IApprovalRepository approvals, IChannelActionRepository actions, TaskRunCoordinator runs, ISecretStore secrets,
    IChannelConversationRepository conversations, Func<System.Net.Http.HttpMessageHandler>? handlerFactory = null) : IOutboundChannelCatalog, IDisposable
{
    private readonly ConcurrentDictionary<Guid, Lazy<TelegramOutboundChannel>> senders = new();
    public async Task<IOutboundChannel?> FindAsync(ChannelRoute route, CancellationToken ct = default)
    {
        var current = (await configurations.ListAsync(owner, ct)).SingleOrDefault(item => item.Route == route && item.Enabled && item.PeerAuthorized);
        if (current is null || current.Route.ChannelId != "telegram" || current.SecretReference is null) return null;
        // Credentials/routes are immutable within an identity. Every adapter attempt/part rechecks
        // current configuration and owner binding; clients are disposed only after host stop.
        return senders.GetOrAdd(current.Id, _ => new Lazy<TelegramOutboundChannel>(() =>
        {
            var policy = new ChannelOwnershipPolicy(owner, bindings, new ConfiguredChannelPolicy(configurations, owner, current.Id));
            return new TelegramOutboundChannel(current.Route.ConnectionId, current.SecretReference, secrets, policy,
                new ChannelApprovalPresenter(policy, approvals, actions, runs: runs), handlerFactory?.Invoke(),
                new ChannelConversationReceiptRecorder(owner, current.Route, current.SenderId, conversations));
        })).Value;
    }
    public void Dispose() { foreach (var sender in senders.Values) if (sender.IsValueCreated) sender.Value.Dispose(); senders.Clear(); }
}

public sealed class ConfiguredDesktopPolicy(IChannelConfigurationRepository configurations, ApprovalOwner owner, ChannelOwnershipPolicy ownership) : IChannelAccessPolicy
{
    public ValueTask<bool> AllowInboundAsync(IncomingMessage message, CancellationToken ct) => ValueTask.FromResult(false);
    public async ValueTask<bool> AllowOutboundAsync(OutgoingMessage message, CancellationToken ct)
    {
        var current = (await configurations.ListAsync(owner, ct)).SingleOrDefault(item => item.Route == message.Destination);
        return (current is null || current.Enabled && current.PeerAuthorized) && await ownership.AllowOutboundAsync(message, ct);
    }
}
