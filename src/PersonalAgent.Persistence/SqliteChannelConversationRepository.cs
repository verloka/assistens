using Microsoft.EntityFrameworkCore;
using PersonalAgent.Core.Channels;
using PersonalAgent.Core.Persistence;
using PersonalAgent.Core.Security;

namespace PersonalAgent.Persistence;

public sealed class SqliteChannelConversationRepository(IDbContextFactory<AgentDbContext> factory) : IChannelConversationRepository
{
    public async Task BindAsync(ChannelConversationLink link, CancellationToken ct = default)
    {
        link.Validate(); await using var db = await factory.CreateDbContextAsync(ct);
        if (!await db.TaskRuns.AnyAsync(run => run.Id == link.RunId && run.ConversationId == link.ConversationId, ct)) throw new ChannelConversationConflictException();
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO ChannelConversationLinks (OwnerSource,OwnerId,ChannelId,ConnectionId,DestinationId,SenderId,ExternalMessageId,ConversationId,RunId,ExternalUpdateId,OutgoingMessageId,CreatedAtUtc,PayloadSchemaVersion)
            VALUES ({link.Owner.Source},{link.Owner.UserId},{link.Route.ChannelId},{link.Route.ConnectionId},{link.Route.DestinationId},{link.SenderId},{link.ExternalMessageId},{link.ConversationId},{link.RunId},{link.ExternalUpdateId},{link.OutgoingMessageId},{link.CreatedAtUtc.UtcTicks},1)
            ON CONFLICT DO NOTHING
            """, ct);
        var saved = await FindAsync(link.Owner, link.Route, link.SenderId, link.ExternalMessageId, ct);
        if (saved is null || saved.ConversationId != link.ConversationId || saved.RunId != link.RunId
            || saved.ExternalUpdateId != link.ExternalUpdateId || saved.OutgoingMessageId != link.OutgoingMessageId) throw new ChannelConversationConflictException();
    }
    public async Task<ChannelConversationLink?> FindAsync(ApprovalOwner owner, ChannelRoute route, string senderId, string externalMessageId, CancellationToken ct = default)
    {
        owner.Validate(); route.Validate();
        if (string.IsNullOrWhiteSpace(senderId) || senderId.Length > 256 || string.IsNullOrWhiteSpace(externalMessageId) || externalMessageId.Length > 256)
            throw new ArgumentException("Invalid external conversation identity.");
        await using var db = await factory.CreateDbContextAsync(ct);
        var row = await db.ChannelConversationLinks.AsNoTracking().SingleOrDefaultAsync(row => row.OwnerSource == owner.Source && row.OwnerId == owner.UserId
            && row.ChannelId == route.ChannelId && row.ConnectionId == route.ConnectionId && row.DestinationId == route.DestinationId
            && row.SenderId == senderId && row.ExternalMessageId == externalMessageId, ct);
        if (row is null) return null;
        if (row.PayloadSchemaVersion != 1) throw new NotSupportedException("Unsupported external conversation link.");
        var link = new ChannelConversationLink(owner, route, row.SenderId, row.ExternalMessageId, row.ConversationId, row.RunId, row.CreatedAtUtc, row.ExternalUpdateId, row.OutgoingMessageId);
        link.Validate();
        if (!await db.TaskRuns.AnyAsync(run => run.Id == link.RunId && run.ConversationId == link.ConversationId, ct)) throw new ChannelConversationConflictException();
        return link;
    }
}
