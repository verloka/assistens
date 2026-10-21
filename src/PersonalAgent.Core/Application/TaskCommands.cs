using PersonalAgent.Core.Agents;
using PersonalAgent.Core.Domain;
using System.Text.Json.Serialization;
using PersonalAgent.Core.Persistence;

namespace PersonalAgent.Core.Application;

public sealed record SubmitTaskCommand(Guid CommandId, string Instructions, TaskOrigin Origin,
    Guid? ConversationId = null, string ModelProfileId = "fake", IReadOnlyList<string>? AllowedToolIds = null, RunLimits? Limits = null,
    RunTriggerKey? ExternalKey = null, ModelProfile? Profile = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] Guid? TaskDefinitionId = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] ScheduleSubmissionClaim? ScheduleClaim = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? SkipReason = null);
public sealed record RunTriggerKey(string Source, string Value);
public sealed record CancelRunCommand(Guid CommandId, Guid RunId, long ExpectedVersion);
public sealed record PauseRunCommand(Guid CommandId, Guid RunId, long ExpectedVersion, PauseScope Scope = PauseScope.EntireRun,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] PauseReason? Reason = null);
public sealed record ResumeRunCommand(Guid CommandId, Guid RunId, long ExpectedVersion);
public sealed record SkipRunCommand(Guid CommandId, Guid RunId, long ExpectedVersion, string Reason);

// Trusted runtime/registry metadata, never permissions supplied by a tool's argument payload.
public sealed record RunStepAccess(bool RequiresDesktop, bool IsReadOnly, bool IndependentOfDesktop)
{
    public static RunStepAccess LocalComputation { get; } = new(false, true, true);
}

public interface ITaskCommands
{
    Task<TaskRun> SubmitAsync(SubmitTaskCommand command, CancellationToken cancellationToken = default);
    Task<TaskRun> CancelAsync(CancelRunCommand command, CancellationToken cancellationToken = default);
    Task<TaskRun> PauseAsync(PauseRunCommand command, CancellationToken cancellationToken = default);
    Task<TaskRun> ResumeAsync(ResumeRunCommand command, CancellationToken cancellationToken = default);
    Task<TaskRun> SkipAsync(SkipRunCommand command, CancellationToken cancellationToken = default);
}
