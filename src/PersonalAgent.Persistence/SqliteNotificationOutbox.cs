using Microsoft.EntityFrameworkCore;
using PersonalAgent.Core.Channels;
using PersonalAgent.Core.Persistence;
using System.Text.Json;
using PersonalAgent.Persistence.Entities;

namespace PersonalAgent.Persistence;

public sealed class SqliteNotificationOutbox(IDbContextFactory<AgentDbContext> factory, TimeProvider? timeProvider = null) : INotificationOutbox
{
    private readonly TimeProvider clock = timeProvider ?? TimeProvider.System;
    public async Task<NotificationDelivery> EnqueueAsync(Guid eventId, OutgoingMessage message, CancellationToken ct = default)
    {
        if (eventId == Guid.Empty) throw new ArgumentException("Notification requires a source event.");
        message = message.Snapshot(); var json = JsonSerializer.Serialize(message); var route = message.Destination; var now = clock.GetUtcNow().ToUniversalTime();
        await using var db = await factory.CreateDbContextAsync(ct);
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        if (await db.TaskRuns.AnyAsync(run => run.ContentClearedAtUtc != null && (run.Id == message.RunId
            || db.RunEvents.Any(evt => evt.Id == eventId && evt.RunId == run.Id)), ct)) throw new NotificationConflictException();
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO NotificationOutbox (Id,EventId,ChannelId,ConnectionId,DestinationId,MessageJson,PayloadSchemaVersion,Status,Attempts,Version,CreatedAtUtc,UpdatedAtUtc,NextAttemptAtUtc)
            VALUES ({message.Id},{eventId},{route.ChannelId},{route.ConnectionId},{route.DestinationId},{json},1,'Pending',0,0,{now.UtcTicks},{now.UtcTicks},{now.UtcTicks})
            ON CONFLICT DO NOTHING
            """, ct);
        var row = await db.NotificationOutbox.AsNoTracking().SingleOrDefaultAsync(row => row.EventId == eventId && row.ChannelId == route.ChannelId
            && row.ConnectionId == route.ConnectionId && row.DestinationId == route.DestinationId, ct);
        if (row is null || row.Id != message.Id || row.MessageJson != json) throw new NotificationConflictException();
        var delivery = Read(row);
        await transaction.CommitAsync(ct);
        return delivery;
    }
    public async Task<NotificationDelivery?> GetAsync(Guid messageId, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct); var row = await db.NotificationOutbox.AsNoTracking().SingleOrDefaultAsync(row => row.Id == messageId, ct);
        return row is null ? null : Read(row);
    }
    public async Task<NotificationDelivery?> ClaimNextAsync(DateTimeOffset now, CancellationToken ct = default)
    {
        now = now.ToUniversalTime(); await using var db = await factory.CreateDbContextAsync(ct);
        var row = await db.NotificationOutbox.AsNoTracking().Where(row => row.Status == NotificationDeliveryStatus.Pending && row.NextAttemptAtUtc <= now)
            .OrderBy(row => row.NextAttemptAtUtc).ThenBy(row => row.CreatedAtUtc).ThenBy(row => row.Id).FirstOrDefaultAsync(ct);
        if (row is null) return null;
        var before = Read(row);
        var updated = await db.NotificationOutbox.Where(item => item.Id == row.Id && item.Status == NotificationDeliveryStatus.Pending && item.Version == row.Version && item.PayloadSchemaVersion == 1)
            .ExecuteUpdateAsync(set => set.SetProperty(item => item.Status, NotificationDeliveryStatus.Sending).SetProperty(item => item.Attempts, row.Attempts + 1)
                .SetProperty(item => item.Version, row.Version + 1).SetProperty(item => item.UpdatedAtUtc, now), ct);
        return updated == 1 ? before with { Status = NotificationDeliveryStatus.Sending, Attempts = checked(before.Attempts + 1), Version = checked(before.Version + 1), UpdatedAtUtc = now } : null;
    }
    public async Task<NotificationDelivery> FinishAsync(Guid messageId, long expectedVersion, NotificationDeliveryStatus status, DateTimeOffset now, DateTimeOffset nextAttemptAt,
        string? errorCode, CancellationToken ct = default)
    {
        if (expectedVersion < 0 || expectedVersion == long.MaxValue || status is not (NotificationDeliveryStatus.Pending or NotificationDeliveryStatus.Delivered or NotificationDeliveryStatus.Failed or NotificationDeliveryStatus.OutcomeUnknown)
            || errorCode is { Length: > 128 } || errorCode?.Any(c => !char.IsAsciiLetterOrDigit(c)) == true) throw new ArgumentException("Invalid notification delivery result.");
        now = now.ToUniversalTime(); nextAttemptAt = nextAttemptAt.ToUniversalTime();
        if (status == NotificationDeliveryStatus.Pending && nextAttemptAt <= now) throw new ArgumentException("Retry requires a future time.");
        await using var db = await factory.CreateDbContextAsync(ct);
        var updated = await db.NotificationOutbox.Where(row => row.Id == messageId && row.Status == NotificationDeliveryStatus.Sending && row.Version == expectedVersion && row.PayloadSchemaVersion == 1)
            .ExecuteUpdateAsync(set => set.SetProperty(row => row.Status, status).SetProperty(row => row.Version, expectedVersion + 1).SetProperty(row => row.UpdatedAtUtc, now)
                .SetProperty(row => row.NextAttemptAtUtc, nextAttemptAt).SetProperty(row => row.ErrorCode, errorCode)
                .SetProperty(row => row.DeliveredAtUtc, status == NotificationDeliveryStatus.Delivered ? (DateTimeOffset?)now : null), ct);
        if (updated != 1) throw new NotificationConflictException();
        return (await GetAsync(messageId, ct))!;
    }
    public async Task RecoverSendingAsync(DateTimeOffset now, CancellationToken ct = default)
    {
        now = now.ToUniversalTime(); await using var db = await factory.CreateDbContextAsync(ct);
        await db.NotificationOutbox.Where(row => row.Status == NotificationDeliveryStatus.Sending && row.PayloadSchemaVersion == 1)
            .ExecuteUpdateAsync(set => set.SetProperty(row => row.Status, NotificationDeliveryStatus.OutcomeUnknown).SetProperty(row => row.ErrorCode, "DeliveryOutcomeUnknown")
                .SetProperty(row => row.UpdatedAtUtc, now).SetProperty(row => row.Version, row => row.Version + 1), ct);
    }
    private static NotificationDelivery Read(NotificationOutboxRow row)
    {
        if (row.PayloadSchemaVersion != 1) throw new NotSupportedException("Unsupported notification schema.");
        var message = (JsonSerializer.Deserialize<OutgoingMessage>(row.MessageJson) ?? throw new NotSupportedException("Invalid notification payload.")).Snapshot();
        if (row.Id != message.Id || row.EventId == Guid.Empty || row.ChannelId != message.Destination.ChannelId || row.ConnectionId != message.Destination.ConnectionId
            || row.DestinationId != message.Destination.DestinationId || !Enum.IsDefined(row.Status) || row.Attempts < 0 || row.Version < 0) throw new NotSupportedException("Invalid notification metadata.");
        return new(row.EventId, message, row.Status, row.Attempts, row.Version, row.CreatedAtUtc, row.UpdatedAtUtc, row.NextAttemptAtUtc, row.ErrorCode, row.DeliveredAtUtc);
    }
}
