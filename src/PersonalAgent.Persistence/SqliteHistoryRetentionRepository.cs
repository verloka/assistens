using Microsoft.EntityFrameworkCore;
using PersonalAgent.Core.Agents;
using PersonalAgent.Core.Channels;
using PersonalAgent.Core.Domain;
using PersonalAgent.Core.Persistence;
using PersonalAgent.Core.Security;
using PersonalAgent.Persistence.Entities;
using System.Text.Json;

namespace PersonalAgent.Persistence;

public sealed class SqliteHistoryRetentionRepository(IDbContextFactory<AgentDbContext> factory) : IHistoryRetentionRepository
{
    public const string ClearedText = "[Private history cleared]";

    public async Task<HistoryRetentionPolicy> ReadPolicyAsync(CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var row = await db.HistoryRetention.AsNoTracking().SingleOrDefaultAsync(ct);
        var policy = row is null ? new HistoryRetentionPolicy() : new(row.Enabled, row.Days);
        policy.Validate();
        return policy;
    }

    public async Task SavePolicyAsync(HistoryRetentionPolicy policy, CancellationToken ct = default)
    {
        policy.Validate();
        await using var db = await factory.CreateDbContextAsync(ct);
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO HistoryRetention (Id, Enabled, Days) VALUES (1, {policy.Enabled}, {policy.Days})
            ON CONFLICT(Id) DO UPDATE SET Enabled = excluded.Enabled, Days = excluded.Days
            """, ct);
    }

    public async Task<IReadOnlyList<StoredRun>> ClearExpiredAsync(DateTimeOffset cutoff, DateTimeOffset now, CancellationToken ct = default)
    {
        if (cutoff >= now) throw new ArgumentOutOfRangeException(nameof(cutoff));
        await using var db = await factory.CreateDbContextAsync(ct);
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var candidates = await db.TaskRuns.Where(run => run.ContentClearedAtUtc == null && run.EndedAtUtc < cutoff)
            .Select(run => run.ConversationId).Distinct().ToArrayAsync(ct);
        var cleared = new List<StoredRun>();
        if (candidates.Length == 0) { await transaction.CommitAsync(ct); return cleared; }
        // Outbox identities need not have an FK to RunEvents; also check explicit run/conversation links.
        var deliveries = await db.NotificationOutbox.ToArrayAsync(ct);
        var messages = deliveries.ToDictionary(row => row.Id, row => JsonSerializer.Deserialize<OutgoingMessage>(row.MessageJson)
            ?? throw new InvalidDataException("Invalid notification payload."));
        foreach (var conversationId in candidates)
        {
            var runs = await db.TaskRuns.Where(run => run.ConversationId == conversationId).ToArrayAsync(ct);
            if (runs.Any(run => run.PayloadSchemaVersion != 1 || run.EndedAtUtc is null || run.EndedAtUtc >= cutoff
                || run.Status is not (RunStatus.Completed or RunStatus.Failed or RunStatus.Cancelled or RunStatus.Skipped)
                || run.ErrorCode?.Contains("Unknown", StringComparison.OrdinalIgnoreCase) == true
                || run.ErrorCode?.Contains("Uncertain", StringComparison.OrdinalIgnoreCase) == true)) continue;
            var ids = runs.Select(run => run.Id).ToArray();
            if (await db.ConversationMessages.AnyAsync(message => message.ConversationId == conversationId && message.TimestampUtc >= cutoff, ct)
                || await db.AgentSessions.AnyAsync(session => session.ConversationId == conversationId && session.TimestampUtc >= cutoff, ct)) continue;
            var calls = await db.ToolCalls.Where(call => ids.Contains(call.RunId)).ToArrayAsync(ct);
            if (calls.Any(call => call.PayloadSchemaVersion != 1 || call.Status != ToolCallDispatchStatus.Acknowledged)) continue;
            var approvals = await db.ApprovalRequests.Where(approval => ids.Contains(approval.RunId)).ToArrayAsync(ct);
            if (approvals.Any(approval => approval.PayloadSchemaVersion != 1 || approval.Status == ApprovalStatus.Pending
                || approval.Status == ApprovalStatus.Approved && !calls.Any(call => call.ApprovalRequestId == approval.Id))) continue;
            var events = await db.RunEvents.Where(evt => ids.Contains(evt.RunId)).OrderBy(evt => evt.Sequence).ToArrayAsync(ct);
            var eventIds = events.Select(evt => evt.Id).ToHashSet();
            var related = deliveries.Where(row => eventIds.Contains(row.EventId) || messages[row.Id].ConversationId == conversationId
                || messages[row.Id].RunId is { } runId && ids.Contains(runId)).ToArray();
            if (related.Any(row => row.PayloadSchemaVersion != 1 || row.Status is not (NotificationDeliveryStatus.Delivered or NotificationDeliveryStatus.Failed))) continue;
            if (related.Any(row => messages[row.Id].RunId is { } runId && !ids.Contains(runId)
                || messages[row.Id].ConversationId is { } linked && linked != conversationId)) continue;

            // Set the privacy marker before redacting events, within this same rollback boundary.
            // The database trigger still forbids changes to event identities or arbitrary payloads.
            await db.TaskRuns.Where(run => ids.Contains(run.Id) && run.ContentClearedAtUtc == null)
                .ExecuteUpdateAsync(set => set.SetProperty(run => run.ContentClearedAtUtc, now), ct);

            foreach (var row in runs.Where(run => run.ContentClearedAtUtc is null))
            {
                var request = new AgentRequest { RunId = row.Id, ConversationId = row.ConversationId,
                    Instructions = ClearedText, ModelProfileId = row.ModelProfileId };
                var result = row.ResultJson is null ? null : JsonSerializer.Deserialize<AgentRunResult>(row.ResultJson)
                    ?? throw new InvalidDataException("Invalid run result.");
                if (result is not null) result = result with { Summary = ClearedText, Sources = [], Artifacts = [], Warnings = [], ErrorCode = null };
                row.Instructions = ClearedText; row.Summary = ClearedText; row.SourcesJson = "[]"; row.Reason = null; row.ErrorCode = null;
                row.RequestJson = JsonSerializer.Serialize(request); row.ResultJson = result is null ? null : JsonSerializer.Serialize(result);
                row.ContentClearedAtUtc = now;
                foreach (var evt in events.Where(evt => evt.RunId == row.Id)) { evt.Message = ClearedText; evt.PayloadJson = null; }
                cleared.Add(new(SqliteRunRepository.ToDomain(row), request,
                    events.Where(evt => evt.RunId == row.Id).Select(SqliteRunRepository.ToDomain).ToArray(), result));
            }
            foreach (var approval in approvals) approval.ActionJson = ""; // Hash/status remain the single-use safety tombstone.
            foreach (var delivery in related)
                delivery.MessageJson = JsonSerializer.Serialize(messages[delivery.Id] with { Text = ClearedText, Actions = [] });
            var history = await db.ConversationMessages.Where(message => message.ConversationId == conversationId).ToArrayAsync(ct);
            foreach (var message in history) { message.Content = ""; message.SourcesJson = "[]"; message.ErrorCode = null; }
            await db.AgentSessions.Where(session => session.ConversationId == conversationId).ExecuteDeleteAsync(ct);
            var conversation = await db.Conversations.SingleAsync(row => row.Id == conversationId, ct);
            conversation.Title = ClearedText;
        }
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return cleared;
    }
}
