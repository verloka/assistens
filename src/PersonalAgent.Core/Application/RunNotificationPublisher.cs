using System.Security.Cryptography;
using System.Text.Json;
using PersonalAgent.Core.Channels;
using PersonalAgent.Core.Domain;
using PersonalAgent.Core.Persistence;

namespace PersonalAgent.Core.Application;

// Consumes durable events; the runner has no channel references or delivery dependency.
public sealed class RunNotificationPublisher(IRunRepository runs, INotificationSubscriptionSource subscriptions,
    INotificationOutbox outbox, IChannelAccessPolicy policy)
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly Dictionary<Guid, (long Version, string Configuration)> published = [];

    public async Task<int> TickAsync(CancellationToken ct = default)
    {
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var routes = (await subscriptions.ListAsync(ct).ConfigureAwait(false)).Select(item => item.Snapshot()).ToArray();
            if (routes.Length > 100) throw new ArgumentException("Too many notification subscriptions.");
            if (routes.Length == 0) return 0;
            var configuration = JsonSerializer.Serialize(routes); var count = 0; var failed = false;
            foreach (var run in await runs.ListAsync(ct).ConfigureAwait(false))
            {
                if (published.TryGetValue(run.Id, out var prior) && prior == (run.RunVersion, configuration)) continue;
                var stored = await runs.GetAsync(run.Id, ct).ConfigureAwait(false);
                if (stored is null) continue;
                var complete = true;
                foreach (var item in stored.Events)
                {
                    var kind = Kind(stored.Run, item);
                    if (kind is null) continue;
                    var destinations = routes.Where(route => route.SinceUtc <= item.TimestampUtc && route.Events.Contains(kind.Value)).Select(route => route.Route).Distinct();
                    foreach (var destination in destinations)
                    {
                        try
                        {
                            var message = Message(item.Id, destination, kind.Value, stored.Run);
                            if (!await policy.AllowOutboundAsync(message, ct).ConfigureAwait(false)) { complete = false; continue; }
                            await outbox.EnqueueAsync(item.Id, message, ct).ConfigureAwait(false); count++;
                        }
                        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                        catch (Exception) { complete = false; failed = true; } // Other destinations still get their independent durable record.
                    }
                }
                if (complete) published[run.Id] = (stored.Run.RunVersion, configuration);
            }
            if (failed) throw new InvalidOperationException("Notification publication is incomplete; independent deliveries remain queued.");
            return count;
        }
        finally { gate.Release(); }
    }

    private static NotificationEventKind? Kind(TaskRun run, RunEvent item)
    {
        if (item.EventType == RunEventType.ApprovalRequested) return NotificationEventKind.ApprovalNeeded;
        if (item.EventType == RunEventType.RunPaused) return NotificationEventKind.PausedForUser;
        RunStatus? status = null;
        if (item.Payload is { ValueKind: JsonValueKind.Object } payload && payload.TryGetProperty("Status", out var value)
            && value.ValueKind == JsonValueKind.String && Enum.TryParse<RunStatus>(value.GetString(), out var parsed)) status = parsed;
        if (item.EventType == RunEventType.RunFinished) return (status ?? run.Status) switch
        { RunStatus.Completed => NotificationEventKind.Completed, RunStatus.Failed => NotificationEventKind.Failed, _ => null };
        return item.EventType == RunEventType.StatusChanged && status == RunStatus.Blocked ? NotificationEventKind.Blocked : null;
    }

    private static OutgoingMessage Message(Guid eventId, ChannelRoute route, NotificationEventKind kind, TaskRun run)
    {
        var id = new Guid(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new { EventId = eventId, Route = route })).AsSpan(0, 16));
        var text = $"{kind}\nRun: {run.Id:D}";
        if (kind == NotificationEventKind.Completed && run.Summary is { Length: > 0 } summary)
        {
            var length = Math.Min(summary.Length, 60_000);
            if (length < summary.Length && char.IsHighSurrogate(summary[length - 1])) length--;
            text += "\n" + summary[..length] + (length < summary.Length ? "\nOpen Personal Agent for the full result." : "");
        }
        else text += "\nOpen Personal Agent for details.";
        return new OutgoingMessage { Id = id, Destination = route, Text = text, RunId = run.Id, ConversationId = run.ConversationId }.Snapshot();
    }
}
