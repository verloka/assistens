using Microsoft.EntityFrameworkCore;
using PersonalAgent.Core.Channels;
using PersonalAgent.Core.Persistence;
using PersonalAgent.Core.Security;
using PersonalAgent.Persistence.Entities;

namespace PersonalAgent.Persistence;

public sealed class SqliteChannelBindingRepository(IDbContextFactory<AgentDbContext> factory, TimeProvider? timeProvider = null) : IChannelBindingRepository
{
    private readonly TimeProvider clock = timeProvider ?? TimeProvider.System;
    public const int MaxPendingPerConnection = 100;
    public async Task<ChannelBinding?> FindAsync(ChannelRoute route, string senderId, CancellationToken ct = default)
    {
        Validate(route, senderId); await using var db = await factory.CreateDbContextAsync(ct);
        var row = await db.ChannelBindings.AsNoTracking().SingleOrDefaultAsync(row => row.ChannelId == route.ChannelId && row.ConnectionId == route.ConnectionId
            && row.DestinationId == route.DestinationId && row.SenderId == senderId, ct);
        return row is null ? null : Read(row);
    }
    public async Task<ChannelBinding> RequestPairingAsync(ChannelRoute route, string senderId, ApprovalOwner owner, CancellationToken ct = default)
    {
        Validate(route, senderId); owner.Validate(); var now = clock.GetUtcNow().ToUniversalTime(); var id = Guid.NewGuid();
        await using var db = await factory.CreateDbContextAsync(ct);
        // One SQLite statement enforces both the peer unique key and the bounded intake even across competing instances.
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO ChannelBindings (Id,PayloadSchemaVersion,ChannelId,ConnectionId,DestinationId,SenderId,OwnerSource,OwnerId,Status,Version,CreatedAtUtc,UpdatedAtUtc)
            SELECT {id},1,{route.ChannelId},{route.ConnectionId},{route.DestinationId},{senderId},{owner.Source},{owner.UserId},'Pending',0,{now.UtcTicks},{now.UtcTicks}
            WHERE (SELECT COUNT(*) FROM ChannelBindings WHERE OwnerSource={owner.Source} AND OwnerId={owner.UserId} AND ChannelId={route.ChannelId} AND ConnectionId={route.ConnectionId} AND Status='Pending') < {MaxPendingPerConnection}
            ON CONFLICT(ChannelId,ConnectionId,DestinationId,SenderId) DO NOTHING
            """, ct);
        var existing = await FindAsync(route, senderId, ct) ?? throw new ChannelPairingLimitException();
        if (existing.Owner != owner) throw new ChannelAccessDeniedException();
        return existing; // Incoming traffic never changes Pending/Approved/Revoked or its revision.
    }
    public async Task<IReadOnlyList<ChannelBinding>> ListAsync(ApprovalOwner owner, CancellationToken ct = default)
    {
        owner.Validate(); await using var db = await factory.CreateDbContextAsync(ct);
        var rows = await db.ChannelBindings.AsNoTracking().Where(row => row.OwnerSource == owner.Source && row.OwnerId == owner.UserId)
            .OrderBy(row => row.CreatedAtUtc).ThenBy(row => row.Id).ToArrayAsync(ct);
        return rows.Select(Read).ToArray();
    }
    public async Task<ChannelBinding> SetStatusAsync(Guid id, long expectedVersion, ApprovalOwner owner, bool approved, CancellationToken ct = default)
    {
        owner.Validate(); if (id == Guid.Empty || expectedVersion < 0) throw new ArgumentException("Invalid channel binding revision.");
        await using var db = await factory.CreateDbContextAsync(ct);
        var row = await db.ChannelBindings.AsNoTracking().SingleOrDefaultAsync(row => row.Id == id, ct);
        if (row is null) throw new ChannelBindingConflictException();
        var before = Read(row);
        if (before.Owner != owner || before.Version != expectedVersion) throw new ChannelBindingConflictException();
        var now = clock.GetUtcNow().ToUniversalTime(); var status = approved ? ChannelBindingStatus.Approved : ChannelBindingStatus.Revoked;
        var count = await db.ChannelBindings.Where(row => row.Id == id && row.PayloadSchemaVersion == 1 && row.Version == expectedVersion
            && row.OwnerSource == owner.Source && row.OwnerId == owner.UserId)
            .ExecuteUpdateAsync(set => set.SetProperty(row => row.Status, status).SetProperty(row => row.Version, row => row.Version + 1)
                .SetProperty(row => row.UpdatedAtUtc, now), ct);
        if (count != 1) throw new ChannelBindingConflictException();
        return before with { Status = status, Version = checked(expectedVersion + 1), UpdatedAtUtc = now };
    }
    private static void Validate(ChannelRoute route, string senderId)
    {
        ArgumentNullException.ThrowIfNull(route); route.Validate();
        if (string.IsNullOrWhiteSpace(senderId) || senderId.Length > 256 || senderId.Any(char.IsControl)) throw new ArgumentException("Invalid channel sender identity.");
    }
    private static ChannelBinding Read(ChannelBindingRow row)
    {
        if (row.PayloadSchemaVersion != 1) throw new NotSupportedException("Unsupported channel binding schema.");
        var route = new ChannelRoute(row.ChannelId, row.ConnectionId, row.DestinationId); Validate(route, row.SenderId);
        var owner = new ApprovalOwner(row.OwnerSource, row.OwnerId); owner.Validate();
        if (row.Id == Guid.Empty || row.Version < 0 || !Enum.IsDefined(row.Status) || row.UpdatedAtUtc < row.CreatedAtUtc) throw new InvalidDataException("Invalid persisted channel binding.");
        return new(row.Id, route, row.SenderId, owner, row.Status, row.Version, row.CreatedAtUtc, row.UpdatedAtUtc);
    }
}
