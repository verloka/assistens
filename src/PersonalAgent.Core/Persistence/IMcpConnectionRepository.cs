using PersonalAgent.Core.Agents;

namespace PersonalAgent.Core.Persistence;

public interface IMcpConnectionRepository
{
    Task<IReadOnlyList<McpConnectionProfile>> ListAsync(CancellationToken cancellationToken = default);
    Task SaveAsync(McpConnectionProfile profile, CancellationToken cancellationToken = default);
    Task DeleteAsync(Guid id, CancellationToken cancellationToken = default);
}
