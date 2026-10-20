using Microsoft.EntityFrameworkCore;
using PersonalAgent.Core.Channels;
using PersonalAgent.Core.Persistence;

namespace PersonalAgent.Persistence;

public sealed class SqliteChannelCursorRepository(IDbContextFactory<AgentDbContext> factory) : IChannelCursorRepository
{
    public async Task<ChannelCursor> ReadAsync(string channelId, string connectionId, CancellationToken ct = default)
    {
        new ChannelRoute(channelId, connectionId, "cursor").Validate();
        await using var db = await factory.CreateDbContextAsync(ct);
        var row = await db.ChannelCursors.AsNoTracking().SingleOrDefaultAsync(row => row.ChannelId == channelId && row.ConnectionId == connectionId, ct);
        if (row is null) return new();
        if (row.PayloadSchemaVersion != 1 || row.NextSequence < 0 || row.Version < 0) throw new NotSupportedException("Unsupported channel receive position.");
        return new(row.NextSequence, row.Version);
    }

    public async Task<ChannelCursor> AdvanceAsync(string channelId, string connectionId, ChannelCursor expected, long nextSequence, CancellationToken ct = default)
    {
        new ChannelRoute(channelId, connectionId, "cursor").Validate();
        if (expected.Version < 0 || expected.Version == long.MaxValue || expected.NextSequence < 0 || nextSequence <= expected.NextSequence)
            throw new ArgumentException("Invalid channel receive position.");
        await using var db = await factory.CreateDbContextAsync(ct);
        // Insert only the initial revision; all advances require an exact, monotonic CAS.
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO ChannelCursors (ChannelId,ConnectionId,NextSequence,Version,PayloadSchemaVersion)
            VALUES ({channelId},{connectionId},0,0,1) ON CONFLICT(ChannelId,ConnectionId) DO NOTHING
            """, ct);
        var changed = await db.ChannelCursors.Where(row => row.ChannelId == channelId && row.ConnectionId == connectionId
            && row.PayloadSchemaVersion == 1 && row.Version == expected.Version && row.NextSequence == expected.NextSequence)
            .ExecuteUpdateAsync(set => set.SetProperty(row => row.NextSequence, nextSequence).SetProperty(row => row.Version, expected.Version + 1), ct);
        if (changed != 1) throw new ChannelCursorConflictException();
        return new(nextSequence, expected.Version + 1);
    }
}
