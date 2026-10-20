using Microsoft.EntityFrameworkCore;
using PersonalAgent.Core.Channels;
using PersonalAgent.Core.Persistence;
using PersonalAgent.Persistence.Entities;
using System.Text.Json;

namespace PersonalAgent.Persistence;

public sealed class SqliteChannelActionRepository(IDbContextFactory<AgentDbContext> factory) : IChannelActionRepository
{
    public async Task SaveAsync(ChannelActionGrant grant, CancellationToken ct = default)
    {
        grant.Validate(); await using var db = await factory.CreateDbContextAsync(ct);
        db.ChannelActions.Add(new ChannelActionRow { TokenHash = grant.TokenHash, ScopeJson = JsonSerializer.Serialize(grant) });
        await db.SaveChangesAsync(ct); // Insert only: an existing token's scope is never replaced.
    }
    public async Task<ChannelActionGrant?> FindAsync(string tokenHash, CancellationToken ct = default)
    {
        if (tokenHash.Length != 64 || tokenHash.Any(c => !char.IsAsciiHexDigit(c))) throw new ArgumentException("Invalid channel action identity.");
        await using var db = await factory.CreateDbContextAsync(ct);
        var row = await db.ChannelActions.AsNoTracking().SingleOrDefaultAsync(row => row.TokenHash == tokenHash, ct);
        if (row is null) return null;
        if (row.PayloadSchemaVersion != 1) throw new NotSupportedException("Unsupported channel action scope.");
        var grant = JsonSerializer.Deserialize<ChannelActionGrant>(row.ScopeJson) ?? throw new NotSupportedException("Invalid channel action scope.");
        grant.Validate();
        if (grant.TokenHash != row.TokenHash) throw new NotSupportedException("Invalid channel action identity.");
        return grant;
    }
}
