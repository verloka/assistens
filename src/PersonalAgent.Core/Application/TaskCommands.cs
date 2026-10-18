using PersonalAgent.Core.Agents;
using PersonalAgent.Core.Domain;

namespace PersonalAgent.Core.Application;

public sealed record SubmitTaskCommand(Guid CommandId, string Instructions, TaskOrigin Origin,
    Guid? ConversationId = null, string ModelProfileId = "fake", IReadOnlyList<string>? AllowedToolIds = null, RunLimits? Limits = null);
public sealed record CancelRunCommand(Guid CommandId, Guid RunId, long ExpectedVersion);
public sealed record PauseRunCommand(Guid CommandId, Guid RunId, long ExpectedVersion, PauseScope Scope = PauseScope.EntireRun);
public sealed record ResumeRunCommand(Guid CommandId, Guid RunId, long ExpectedVersion);

public interface ITaskCommands
{
    Task<TaskRun> SubmitAsync(SubmitTaskCommand command, CancellationToken cancellationToken = default);
    Task<TaskRun> CancelAsync(CancelRunCommand command, CancellationToken cancellationToken = default);
    Task<TaskRun> PauseAsync(PauseRunCommand command, CancellationToken cancellationToken = default);
    Task<TaskRun> ResumeAsync(ResumeRunCommand command, CancellationToken cancellationToken = default);
}
