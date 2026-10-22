using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using PersonalAgent.Core.Channels;
using PersonalAgent.Core.Persistence;
using PersonalAgent.Core.Security;
using PersonalAgent.Persistence.Entities;

namespace PersonalAgent.Persistence;

public sealed class SqliteChannelConfigurationRepository(IDbContextFactory<AgentDbContext> factory) : IChannelConfigurationRepository
{
    public async Task<IReadOnlyList<ChannelConfiguration>> ListAsync(ApprovalOwner owner, CancellationToken ct = default)
    {
        owner.Validate(); await using var db = await factory.CreateDbContextAsync(ct);
        return (await db.ChannelConfigurations.AsNoTracking().Where(row => row.OwnerSource == owner.Source && row.OwnerId == owner.UserId).OrderBy(row => row.Id).ToArrayAsync(ct)).Select(Read).ToArray();
    }
    public async Task<ChannelConfiguration> SaveAsync(ChannelConfiguration configuration, long expectedVersion, CancellationToken ct = default)
    {
        configuration = configuration.Snapshot();
        if (configuration.Version != expectedVersion) throw new ChannelConfigurationConflictException();
        await using var db = await factory.CreateDbContextAsync(ct);
        var row = await db.ChannelConfigurations.SingleOrDefaultAsync(row => row.Id == configuration.Id, ct);
        if (row is null)
        {
            if (expectedVersion != 0) throw new ChannelConfigurationConflictException();
            db.ChannelConfigurations.Add(row = new() { Id = configuration.Id, OwnerSource = configuration.Owner.Source, OwnerId = configuration.Owner.UserId,
                ChannelId = configuration.Route.ChannelId, ConnectionId = configuration.Route.ConnectionId, DestinationId = configuration.Route.DestinationId, ConfigurationJson = "" });
        }
        else
        {
            var prior = Read(row);
            if (prior.Version != expectedVersion || prior.Owner != configuration.Owner || prior.Route != configuration.Route || prior.SenderId != configuration.SenderId
                || prior.SecretReference != configuration.SecretReference && !(prior.SecretReference is null && !prior.Enabled && prior.RemoteIdentity is null && !configuration.Enabled)
                || configuration.SinceUtc < prior.SinceUtc
                || prior.RemoteIdentity is not null && prior.RemoteIdentity != configuration.RemoteIdentity) throw new ChannelConfigurationConflictException();
        }
        var saved = configuration with { Version = checked(expectedVersion + 1) };
        row.Version = saved.Version; row.Enabled = saved.Enabled; row.RemoteIdentity = saved.RemoteIdentity; row.ConfigurationJson = JsonSerializer.Serialize(saved);
        try { await db.SaveChangesAsync(ct); }
        catch (Exception error) when (error is DbUpdateConcurrencyException || error is DbUpdateException { InnerException: Microsoft.Data.Sqlite.SqliteException { SqliteErrorCode: 19 } })
        { throw new ChannelConfigurationConflictException(); }
        return saved;
    }
    private static ChannelConfiguration Read(ChannelConfigurationRow row)
    {
        if (row.PayloadSchemaVersion != 1) throw new NotSupportedException("Unsupported channel configuration.");
        var value = (JsonSerializer.Deserialize<ChannelConfiguration>(row.ConfigurationJson) ?? throw new NotSupportedException("Invalid channel configuration.")).Snapshot();
        if (value.Id != row.Id || value.Owner != new ApprovalOwner(row.OwnerSource, row.OwnerId) || value.Version != row.Version
            || value.Route != new ChannelRoute(row.ChannelId, row.ConnectionId, row.DestinationId) || value.Enabled != row.Enabled || value.RemoteIdentity != row.RemoteIdentity)
            throw new NotSupportedException("Invalid channel configuration identity.");
        return value;
    }
}
