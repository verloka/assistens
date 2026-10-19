using PersonalAgent.Core.Agents;

namespace PersonalAgent.Core.Persistence;

public interface IModelProfileRepository
{
    Task<IReadOnlyList<ModelProfile>> ListAsync(CancellationToken cancellationToken = default);
    Task SaveAsync(ModelProfile profile, CancellationToken cancellationToken = default);
}
