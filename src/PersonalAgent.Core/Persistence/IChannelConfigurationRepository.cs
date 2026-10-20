using PersonalAgent.Core.Channels;
using PersonalAgent.Core.Security;

namespace PersonalAgent.Core.Persistence;

public interface IChannelConfigurationRepository
{
    Task<IReadOnlyList<ChannelConfiguration>> ListAsync(ApprovalOwner owner, CancellationToken ct = default);
    Task<ChannelConfiguration> SaveAsync(ChannelConfiguration configuration, long expectedVersion, CancellationToken ct = default);
}
