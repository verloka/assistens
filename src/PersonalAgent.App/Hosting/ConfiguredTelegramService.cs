using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using PersonalAgent.Channels.Telegram;
using PersonalAgent.Core.Application;
using PersonalAgent.Core.Channels;
using PersonalAgent.Core.Persistence;
using PersonalAgent.Core.Security;

namespace PersonalAgent.App;

public sealed class ConfiguredTelegramService(IChannelConfigurationRepository configurations, ApprovalOwner owner, IChannelBindingRepository bindings,
    IApprovalRepository approvals, IChannelActionRepository actions, IChannelCursorRepository cursors, INotificationOutbox outbox,
    TaskRunCoordinator runs, ChannelMessageRouter router, ISecretStore secrets, ILogger<ConfiguredTelegramService> logger,
    Func<System.Net.Http.HttpMessageHandler>? handlerFactory = null) : BackgroundService
{
    private sealed record Receiver(long Version, CancellationTokenSource Stop, Task Task);
    private readonly Dictionary<Guid, Receiver> receivers = [];
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        try
        {
            do
            {
                try
                {
                    var current = (await configurations.ListAsync(owner, stoppingToken)).Where(item => item.Enabled && item.PeerAuthorized && item.ReceiveTasks && item.Route.ChannelId == "telegram").ToDictionary(item => item.Id);
                    foreach (var id in receivers.Keys.ToArray())
                    {
                        var receiver = receivers[id];
                        if (current.TryGetValue(id, out var saved) && saved.Version == receiver.Version) continue;
                        receiver.Stop.Cancel(); await receiver.Task; receiver.Stop.Dispose(); receivers.Remove(id);
                    }
                    foreach (var saved in current.Values.Where(item => !receivers.ContainsKey(item.Id)))
                    {
                        var stop = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                        receivers.Add(saved.Id, new(saved.Version, stop, ReceiveAsync(saved, stop.Token)));
                    }
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
                catch (Exception) { logger.LogWarning("Channel configuration is unavailable; no new receiver was started."); }
            } while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        finally
        {
            foreach (var receiver in receivers.Values) receiver.Stop.Cancel();
            foreach (var receiver in receivers.Values) { await receiver.Task; receiver.Stop.Dispose(); }
            receivers.Clear();
        }
    }
    private async Task ReceiveAsync(ChannelConfiguration saved, CancellationToken ct)
    {
        try
        {
            if (saved.SecretReference is null) return;
            var policy = new ChannelOwnershipPolicy(owner, bindings, new ConfiguredChannelPolicy(configurations, owner, saved.Id));
            var commands = new ChannelOwnerCommands(policy, runs, approvals);
            var handler = new TelegramCommandHandler(saved.Route.ConnectionId, policy, commands, router, runs,
                botUsername: saved.RemoteUsername,
                actions: new ChannelActionHandler(policy, actions, approvals, commands, runs));
            using var receiver = new TelegramPollingReceiver(new(saved.Route.ConnectionId, saved.SecretReference), secrets, cursors, handlerFactory?.Invoke());
            await receiver.RunAsync(async (message, token) =>
            {
                await policy.RequireOwnerAsync(message, token);
                var replyId = TelegramCommandHandler.ReplyIdentity(message);
                if (await outbox.GetAsync(replyId, token) is not null) return;
                var reply = await handler.HandleAsync(message, token);
                await outbox.EnqueueAsync(reply.Id, reply, token);
            }, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (TelegramPollingException error) { logger.LogWarning("Telegram receiving stopped ({Code}); review configuration and save to restart.", error.Code); }
        catch (Exception) { logger.LogWarning("Telegram receiving stopped safely; review configuration and save to restart."); }
        // Retain the completed receiver/revision: failed configurations never busy-restart.
    }
}
