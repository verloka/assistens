using PersonalAgent.Core.Agents;
using PersonalAgent.Core.Persistence;

namespace PersonalAgent.Agent;

// A run carries its immutable configuration. Changing selection/editor state cannot change a queued run.
public sealed class ProfileAgentRuntime(IAgentRuntime localRuntime, IEnumerable<IModelProvider> providers,
    Func<Guid, CancellationToken, Task> waitAtBoundary, IConversationRepository? sessions = null,
    Func<IModelProvider, ModelProfile, AgentRequest, IProgress<AgentEvent>, CancellationToken, Task<AgentRunResult>>? executeWithTools = null) : IAgentRuntime
{
    private readonly IReadOnlyDictionary<string, IModelProvider> available = providers.ToDictionary(p => p.ProviderId, StringComparer.Ordinal);

    public Task<AgentRunResult> ExecuteAsync(AgentRequest request, IProgress<AgentEvent> progress, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (request.ModelProfileId == "fake" && request.Profile is null && request.AllowedToolIds.Count == 0 && request.ToolPermissions is null)
            return localRuntime.ExecuteAsync(request, progress, cancellationToken);
        if (request.Profile is not { } profile || profile.Id != request.ModelProfileId || profile.Id == "fake"
            || !available.TryGetValue(profile.ProviderId, out var provider))
            return Blocked("ModelProfileUnavailable", "The selected model profile is unavailable; no provider call was started.");
        if (request.AllowedToolIds.Count != 0)
            return executeWithTools is not null && request.ToolPermissions is { Count: > 0 }
                ? executeWithTools(provider, profile, request, progress, cancellationToken)
                : Blocked("ToolRegistryUnavailable", "Tool permissions are not configured for this task; no provider call was started.");
        if (request.ToolPermissions is not null) return Blocked("ToolRegistryUnavailable", "The tool selection does not match this task.");
        return new MafAgentRuntime(provider, profile, requiresDesktop: false, waitAtBoundary, sessions).ExecuteAsync(request, progress, cancellationToken);
    }

    private static Task<AgentRunResult> Blocked(string code, string message) => Task.FromResult(new AgentRunResult
    { Status = AgentResultStatus.Blocked, ErrorCode = code, Summary = message });
}
