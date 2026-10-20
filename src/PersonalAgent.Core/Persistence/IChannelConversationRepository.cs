using PersonalAgent.Core.Channels;
using PersonalAgent.Core.Security;

namespace PersonalAgent.Core.Persistence;

public interface IChannelConversationRepository
{
    Task BindAsync(ChannelConversationLink link, CancellationToken ct = default);
    Task<ChannelConversationLink?> FindAsync(ApprovalOwner owner, ChannelRoute route, string senderId, string externalMessageId, CancellationToken ct = default);
}
