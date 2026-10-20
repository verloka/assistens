using PersonalAgent.Core.Channels;

namespace PersonalAgent.Core.Persistence;

public interface IChannelActionRepository
{
    Task SaveAsync(ChannelActionGrant grant, CancellationToken ct = default);
    Task<ChannelActionGrant?> FindAsync(string tokenHash, CancellationToken ct = default);
}
