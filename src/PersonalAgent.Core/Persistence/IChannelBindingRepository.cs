using PersonalAgent.Core.Channels;
using PersonalAgent.Core.Security;

namespace PersonalAgent.Core.Persistence;

public interface IChannelBindingRepository
{
    Task<ChannelBinding?> FindAsync(ChannelRoute route, string senderId, CancellationToken ct = default);
    Task<ChannelBinding> RequestPairingAsync(ChannelRoute route, string senderId, ApprovalOwner owner, CancellationToken ct = default);
    Task<IReadOnlyList<ChannelBinding>> ListAsync(ApprovalOwner owner, CancellationToken ct = default);
    Task<ChannelBinding> SetStatusAsync(Guid id, long expectedVersion, ApprovalOwner owner, bool approved, CancellationToken ct = default);
}
