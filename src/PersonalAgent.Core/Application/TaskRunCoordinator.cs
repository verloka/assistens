using PersonalAgent.Core.Agents;
using PersonalAgent.Core.Domain;
using PersonalAgent.Core.Persistence;
using PersonalAgent.Core.Computer;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;

namespace PersonalAgent.Core.Application;

// Persist each revision before publishing it. Query locks never cover database I/O.
// Native desktop lease/generation gating remains a separate resource boundary.
public sealed class TaskRunCoordinator(IRunRepository? repository = null, IDesktopAccessCoordinator? desktopAccess = null,
    IConversationRepository? conversations = null) : ITaskCommands, IDisposable
{
    private readonly object sync = new();
    private readonly SemaphoreSlim writes = new(1, 1);
    private readonly Dictionary<Guid, Entry> runs = [];
    private readonly Dictionary<Guid, (string Fingerprint, Guid RunId)> receipts = [];
    private readonly Dictionary<(string Source, string Key), (string Hash, Guid RunId)> submissions = [];
    private readonly Channel<Guid> queue = Channel.CreateUnbounded<Guid>(new UnboundedChannelOptions { SingleReader = true });
    private bool disposed, runnerAttached;

    public async Task<TaskRun> SubmitAsync(SubmitTaskCommand command, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentException.ThrowIfNullOrWhiteSpace(command.Instructions);
        ArgumentException.ThrowIfNullOrWhiteSpace(command.ModelProfileId);
        ArgumentException.ThrowIfNullOrWhiteSpace(command.Origin.Source);
        if (command.Profile is { } profile && (profile.Id != command.ModelProfileId || profile.Id == "fake"))
            throw new ArgumentException("The profile snapshot must match the selected cloud profile.");
        (command.Limits ?? new()).Validate();
        if (command.ConversationId == Guid.Empty) throw new ArgumentException("Conversation ID cannot be empty.");
        if (command.TaskDefinitionId == Guid.Empty) throw new ArgumentException("Task definition ID cannot be empty.");
        if (command.SkipReason is { } skipReason && (string.IsNullOrWhiteSpace(skipReason) || skipReason.Length > 512)) throw new ArgumentException("Invalid skip reason.");
        if (command.ScheduleClaim is { } claim && (command.TaskDefinitionId is null || claim.ExpectedVersion < 0
            || command.ExternalKey != ScheduleOccurrenceIdentity.Create(claim.ScheduleId, claim.OccurrenceUtc)))
            throw new ArgumentException("Scheduled admission must match the exact task and occurrence trigger.");
        if (command.ExternalKey is { } external)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(external.Source);
            ArgumentException.ThrowIfNullOrWhiteSpace(external.Value);
        }
        var identity = SubmissionIdentity(command);
        await writes.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var fingerprint = Fingerprint(command);
            lock (sync)
            {
                ObjectDisposedException.ThrowIf(disposed, this);
                if (FindReceipt(command.CommandId, fingerprint) is { } existing) return existing;
                if (submissions.TryGetValue((identity.Source, identity.Key), out var prior))
                {
                    if (prior.Hash != identity.IntentHash) throw new RunSubmissionConflictException();
                    receipts.Add(command.CommandId, (fingerprint, prior.RunId));
                    return Find(prior.RunId).Run;
                }
            }
            var context = await ReadConversationContextAsync(command.ConversationId, cancellationToken).ConfigureAwait(false);
            var now = DateTimeOffset.UtcNow;
            var run = new TaskRun
            {
                Id = Guid.NewGuid(), TaskDefinitionId = command.TaskDefinitionId, ConversationId = command.ConversationId ?? Guid.NewGuid(), Origin = command.Origin,
                Instructions = command.Instructions, ModelProfileId = command.ModelProfileId, CreatedAtUtc = now
            };
            var request = new AgentRequest
            {
                RunId = run.Id, ConversationId = run.ConversationId, Instructions = run.Instructions,
                ModelProfileId = run.ModelProfileId, Profile = command.Profile,
                AllowedToolIds = command.AllowedToolIds?.ToArray() ?? [], Limits = command.Limits ?? new(), Context = context
            };
            var change = new Change(new Entry(run, request));
            AddEvent(change, RunEventType.RunCreated, "Task created");
            if (command.SkipReason is { } reason)
            {
                change.Run = change.Run with { Summary = reason };
                Move(change, RunStatus.Skipped, reason);
            }
            else Move(change, RunStatus.Queued, "Task queued");
            if (repository is not null)
            {
                var conversation = new Conversation { Id = run.ConversationId, Title = run.Instructions, CreatedAtUtc = now, UpdatedAtUtc = now };
                StoredRun? stored = null;
                await PersistAsync(async () => stored = await repository.GetOrCreateAsync(change.Run, conversation, request, change.Events, identity, cancellationToken), cancellationToken).ConfigureAwait(false);
                if (stored!.Run.Id != run.Id)
                {
                    lock (sync)
                    {
                        if (!runs.TryGetValue(stored.Run.Id, out var prior))
                        {
                            prior = new Entry(stored.Run, stored.Request) { Result = stored.Result };
                            prior.Events.AddRange(stored.Events);
                            if (TaskRunStateMachine.IsTerminal(prior.Run.Status)) prior.Terminal.TrySetResult(prior.Run);
                            runs.Add(prior.Run.Id, prior);
                            // Lost creation acknowledgement can leave a committed but unqueued local task.
                            // Only its proven unstarted/no-tool first execution may be queued here.
                            if (prior.Run.Status == RunStatus.Queued && prior.Run.StartedAtUtc is null && prior.Request.ModelProfileId == "fake" && prior.Request.AllowedToolIds.Count == 0)
                                queue.Writer.TryWrite(prior.Run.Id);
                        }
                        receipts.Add(command.CommandId, (fingerprint, prior.Run.Id));
                        submissions[(identity.Source, identity.Key)] = (identity.IntentHash, prior.Run.Id);
                        return prior.Run;
                    }
                }
            }
            lock (sync)
            {
                ObjectDisposedException.ThrowIf(disposed, this);
                runs.Add(run.Id, change.Entry);
                Commit(change);
                receipts.Add(command.CommandId, (fingerprint, run.Id));
                submissions.Add((identity.Source, identity.Key), (identity.IntentHash, run.Id));
                if (change.Run.Status == RunStatus.Queued) queue.Writer.TryWrite(run.Id);
                return change.Entry.Run;
            }
        }
        finally { writes.Release(); }
    }

    public Task<TaskRun> CancelAsync(CancelRunCommand command, CancellationToken cancellationToken = default) =>
        Apply(command.CommandId, command.RunId, command.ExpectedVersion, command, change =>
        {
            if (TaskRunStateMachine.IsTerminal(change.Run.Status)) return;
            change.DesktopDrain = desktopAccess?.PauseRunAsync(change.Run.Id);
            change.Run = change.Run with { Summary = null, ErrorCode = null, Sources = [] };
            change.Result = null;
            Move(change, RunStatus.Cancelled, "Cancelled by user");
            change.Cancel = true; change.Wake = true;
        }, cancellationToken);

    public Task<TaskRun> PauseAsync(PauseRunCommand command, CancellationToken cancellationToken = default) =>
        PauseCore(command, cancellationToken);

    // A trusted local takeover is bound to the exact closed desktop gate, not to a progress revision.
    // No command/tool replay or stale user-resume override is permitted.
    public Task<TaskRun> PauseForUserActivityAsync(Guid runId, long expectedPauseVersion, CancellationToken ct = default) =>
        PauseCore(new(Guid.NewGuid(), runId, 0, PauseScope.EntireRun, PauseReason.AutoPause), ct, expectedPauseVersion);

    public Task<TaskRun> PauseForDesktopEnvironmentChangeAsync(Guid runId, long expectedPauseVersion, CancellationToken ct = default) =>
        PauseCore(new(Guid.NewGuid(), runId, 0, PauseScope.EntireRun, PauseReason.DesktopEnvironmentChanged), ct, expectedPauseVersion);

    private Task<TaskRun> PauseCore(PauseRunCommand command, CancellationToken cancellationToken, long? activityPauseVersion = null) =>
        ChangeAsync(command.RunId, change =>
        {
            if (activityPauseVersion is { } expected && (desktopAccess?.Snapshot is not { } state
                || state.PauseVersion != expected || state.HumanControlOwnerRunId != command.RunId
                || state.State is not (DesktopAccessState.Pausing or DesktopAccessState.PausedForUser)))
                throw new DesktopAccessException("ActivityTakeoverSuperseded");
            if (!Enum.IsDefined(command.Scope)) throw new ArgumentOutOfRangeException(nameof(command.Scope));
            if (command.Reason is { } reason && (!Enum.IsDefined(reason) || command.Scope != PauseScope.EntireRun))
                throw new ArgumentException("Automatic activity pause requires the entire run scope.");
            if (TaskRunStateMachine.IsTerminal(change.Run.Status)) throw new InvalidOperationException("Cannot pause a terminal run.");
            if (change.Run.PauseScope is { } scope)
            {
                if (scope != command.Scope) throw new InvalidOperationException("Resume before changing pause scope.");
                return;
            }
            _ = desktopAccess?.PauseRunAsync(change.Run.Id);
            if (command.Scope == PauseScope.GuiOnly)
            {
                change.Run = change.Run with { PauseScope = command.Scope, RunVersion = checked(change.Run.RunVersion + 1) };
                change.NewResume = Signal();
                AddEvent(change, RunEventType.DesktopTakenOver, "GUI-only pause requested; new desktop actions blocked");
                return;
            }
            Move(change, change.Run.Status == RunStatus.Queued ? RunStatus.Paused : RunStatus.Pausing, command.Reason?.ToString() ?? "Pause requested", command.Scope);
            change.NewResume = Signal();
        }, cancellationToken, command.CommandId, Fingerprint(command), activityPauseVersion is null ? command.ExpectedVersion : null);

    public async Task<TaskRun> ConfirmDesktopPauseAsync(Guid runId, CancellationToken cancellationToken = default)
    {
        if (desktopAccess is null) throw new InvalidOperationException("No desktop resource is configured.");
        if (GetRun(runId).PauseScope is null) throw new InvalidOperationException("Run is not paused.");
        var released = await desktopAccess.ReleasePausedRunAsync(runId, cancellationToken).ConfigureAwait(false);
        return await ChangeAsync(runId, change =>
        {
            if (TaskRunStateMachine.IsTerminal(change.Run.Status) || change.Run.PauseScope is null)
                throw new InvalidOperationException("Run is not paused.");
            var current = desktopAccess.Snapshot;
            if (current.InputCleanupUncertain || current.Generation != released.Generation || current.OwnerRunId is not null)
                throw new DesktopAccessException("DesktopPauseChanged");
            var last = change.Entry.Events.LastOrDefault(e => e.EventType is RunEventType.DesktopPauseConfirmed or RunEventType.RunResumed);
            if (last?.EventType == RunEventType.DesktopPauseConfirmed && last.Payload is { } payload &&
                payload.TryGetProperty("Generation", out var generation) && generation.GetInt64() == released.Generation) return;
            change.Run = change.Run with { RunVersion = checked(change.Run.RunVersion + 1) };
            AddEvent(change, RunEventType.DesktopPauseConfirmed, "Desktop input drained and lease released; explicit resume required");
            change.Events[^1] = change.Events[^1] with { Payload = JsonSerializer.SerializeToElement(new
            {
                released.Generation, InputReleased = true, Reason = change.Run.Reason switch
                { nameof(PauseReason.AutoPause) => "UserActivity", nameof(PauseReason.DesktopEnvironmentChanged) => "DesktopEnvironmentChanged", _ => "UserTakeControl" }
            }) };
        }, cancellationToken).ConfigureAwait(false);
    }

    public Task<TaskRun> BlockUncertainInputAsync(Guid runId, CancellationToken cancellationToken = default) =>
        ChangeAsync(runId, change =>
        {
            if (TaskRunStateMachine.IsTerminal(change.Run.Status)) return;
            if (desktopAccess?.Snapshot.InputCleanupUncertain != true)
                throw new InvalidOperationException("No uncertain desktop input was reported.");
            if (change.Run.Status == RunStatus.Blocked && change.Run.ErrorCode == "InputCleanupUncertain") return;
            change.Run = change.Run with { ErrorCode = "InputCleanupUncertain" };
            Move(change, RunStatus.Blocked, "Input release could not be confirmed; manual review required");
        }, cancellationToken);

    public Task<TaskRun> ResumeAsync(ResumeRunCommand command, CancellationToken cancellationToken = default) =>
        Apply(command.CommandId, command.RunId, command.ExpectedVersion, command, change =>
        {
            if (TaskRunStateMachine.IsTerminal(change.Run.Status)) throw new InvalidOperationException("Cannot resume a terminal run.");
            if (change.Run.Status == RunStatus.Pausing) throw new InvalidOperationException("Wait for the current step to reach the pause boundary.");
            if (change.Run.Status is not (RunStatus.Paused or RunStatus.PausedForUser or RunStatus.Running) && change.Run.PauseScope != PauseScope.GuiOnly)
                throw new InvalidOperationException("Run is not paused.");
            if (change.Run.Status == RunStatus.Running && change.Run.PauseScope is null) return; // Idempotent task resume grants no GUI permission.
            change.ResumeDesktopVersion = desktopAccess?.ValidateResumeRun(change.Run.Id);
            if (change.Run.Status is RunStatus.Paused or RunStatus.PausedForUser)
            {
                Move(change, change.Run.StartedAtUtc is null ? RunStatus.Queued : RunStatus.Running, "Task resumed");
                change.Wake = true;
            }
            else if (change.Run.PauseScope == PauseScope.GuiOnly)
            {
                change.Run = change.Run with { PauseScope = null, RunVersion = checked(change.Run.RunVersion + 1) };
                AddEvent(change, RunEventType.RunResumed, "GUI-only pause cleared; fresh desktop context is still required before GUI use");
                change.Wake = true;
            }
            else if (change.Run.Status != RunStatus.Running) throw new InvalidOperationException("Run is not paused.");
        }, cancellationToken);

    public Task<TaskRun> SkipAsync(SkipRunCommand command, CancellationToken cancellationToken = default) =>
        Apply(command.CommandId, command.RunId, command.ExpectedVersion, command, change =>
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(command.Reason);
            if (change.Run.Status != RunStatus.Queued || change.Run.StartedAtUtc is not null)
                throw new InvalidOperationException("Only an unstarted queued occurrence can be skipped.");
            change.DesktopDrain = desktopAccess?.PauseRunAsync(change.Run.Id);
            Move(change, RunStatus.Skipped, command.Reason);
            change.Cancel = true; change.Wake = true;
        }, cancellationToken);

    public TaskRun GetRun(Guid runId) { lock (sync) return Find(runId).Run; }
    public IReadOnlyList<TaskRun> GetRuns() { lock (sync) return runs.Values.Select(entry => entry.Run).OrderByDescending(run => run.CreatedAtUtc).ToArray(); }
    public IReadOnlyList<RunEvent> GetEvents(Guid runId) { lock (sync) return Find(runId).Events.ToArray(); }
    public AgentRunResult? GetResult(Guid runId) { lock (sync) return Find(runId).Result; }
    public Task<TaskRun> WaitForTerminalAsync(Guid runId, CancellationToken cancellationToken = default)
    { lock (sync) return Find(runId).Terminal.Task.WaitAsync(cancellationToken); }

    public Task LoadFinishedHistoryAsync(CancellationToken cancellationToken = default) => LoadHistoryAsync([], false, cancellationToken);
    public Task RestoreHistoryAsync(IReadOnlyList<Guid> safeQueuedRuns, CancellationToken cancellationToken = default) => LoadHistoryAsync(safeQueuedRuns, true, cancellationToken);

    private async Task LoadHistoryAsync(IReadOnlyList<Guid> safeQueuedRuns, bool includePending, CancellationToken cancellationToken)
    {
        if (repository is null) return;
        await writes.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var summaries = await Task.Run(() => repository.ListAsync(cancellationToken), cancellationToken).ConfigureAwait(false);
            foreach (var summary in summaries.Where(run => includePending || TaskRunStateMachine.IsTerminal(run.Status)))
            {
                var stored = await Task.Run(() => repository.GetAsync(summary.Id, cancellationToken), cancellationToken).ConfigureAwait(false);
                if (stored is null) continue;
                lock (sync)
                {
                    if (runs.ContainsKey(summary.Id)) continue;
                    var entry = new Entry(stored.Run, stored.Request) { Result = stored.Result };
                    entry.Events.AddRange(stored.Events);
                    if (TaskRunStateMachine.IsTerminal(entry.Run.Status)) entry.Terminal.TrySetResult(entry.Run);
                    runs.Add(entry.Run.Id, entry);
                    if (entry.Run.ErrorCode == "InputCleanupUncertain")
                        desktopAccess?.RestoreInputCleanupFailure(entry.Run.Id);
                    if (entry.Run.PauseScope is not null && !TaskRunStateMachine.IsTerminal(entry.Run.Status))
                        _ = desktopAccess?.PauseRunAsync(entry.Run.Id);
                    if (safeQueuedRuns.Contains(entry.Run.Id))
                    {
                        if (entry.Run.Status != RunStatus.Queued || entry.Request.ModelProfileId != "fake" || entry.Request.AllowedToolIds.Count != 0)
                            throw new InvalidOperationException("Recovered work is not a known-safe unstarted local task.");
                        queue.Writer.TryWrite(entry.Run.Id);
                    }
                }
            }
        }
        finally { writes.Release(); }
    }

    public async IAsyncEnumerable<RunEvent> WatchRunAsync(Guid runId, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var updates = Channel.CreateUnbounded<RunEvent>(new UnboundedChannelOptions { SingleReader = true });
        lock (sync)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            var entry = Find(runId);
            foreach (var value in entry.Events) updates.Writer.TryWrite(value);
            if (TaskRunStateMachine.IsTerminal(entry.Run.Status)) updates.Writer.TryComplete();
            else entry.Watchers.Add(updates);
        }
        try { await foreach (var value in updates.Reader.ReadAllAsync(cancellationToken)) yield return value; }
        finally { lock (sync) Find(runId).Watchers.Remove(updates); }
    }

    internal ChannelReader<Guid> AttachRunner()
    {
        lock (sync)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (runnerAttached) throw new InvalidOperationException("A runner is already attached.");
            runnerAttached = true; return queue.Reader;
        }
    }
    internal void DetachRunner() { lock (sync) runnerAttached = false; }
    internal (AgentRequest Request, CancellationToken Cancellation) GetExecution(Guid runId)
    { lock (sync) { var entry = Find(runId); return (entry.Request, entry.Cancellation.Token); } }

    internal async Task<bool> StartExecutionAsync(Guid runId, CancellationToken cancellationToken)
    {
        while (true)
        {
            await WaitAtBoundaryAsync(runId, cancellationToken).ConfigureAwait(false);
            var started = false;
            await ChangeAsync(runId, change =>
            {
                if (change.Run.Status != RunStatus.Queued || change.Entry.Executing) return;
                Move(change, RunStatus.Running, "Task started"); change.Executing = true; started = true;
            }, cancellationToken).ConfigureAwait(false);
            if (started) return true;
            lock (sync) if (TaskRunStateMachine.IsTerminal(Find(runId).Run.Status) || Find(runId).Executing) return false;
        }
    }
    public Task<TaskRun> BeginScheduledDesktopWaitAsync(Guid runId, long expectedVersion, CancellationToken ct = default) =>
        ChangeAsync(runId, change =>
        {
            if (change.Run.Origin.Source != "schedule" || change.Run.Status != RunStatus.Running || change.Run.PauseScope is not null)
                throw new InvalidOperationException("Only an active scheduled run can wait for desktop admission.");
            Move(change, RunStatus.WaitingForDesktop, "Waiting for scheduled desktop warning and permission");
        }, ct, expectedVersion: expectedVersion);

    public Task<TaskRun> EndScheduledDesktopWaitAsync(Guid runId, long expectedVersion, bool skip, CancellationToken ct = default, bool expired = false) =>
        ChangeAsync(runId, change =>
        {
            if (change.Run.Origin.Source != "schedule" || change.Run.Status != RunStatus.WaitingForDesktop || change.Run.PauseScope is not null)
                throw new InvalidOperationException("Scheduled desktop admission is no longer current.");
            if (expired && !skip) throw new ArgumentException("An expired desktop wait cannot authorize acquisition.");
            if (expired) change.Run = change.Run with { ErrorCode = "DeferredExpired" };
            Move(change, skip ? RunStatus.Skipped : RunStatus.Running, expired ? "DeferredExpired: scheduled desktop wait exceeded its maximum duration"
                : skip ? "Scheduled desktop occurrence skipped by owner" : "Scheduled desktop warning permitted acquisition; resource checks still apply");
            if (skip) { change.DesktopDrain = desktopAccess?.PauseRunAsync(runId); change.Cancel = true; change.Wake = true; }
        }, ct, expectedVersion: expectedVersion);

    public Task<TaskRun> BeginToolApprovalAsync(Guid runId, long expectedVersion, string toolId, CancellationToken ct = default) =>
        ChangeAsync(runId, change =>
        {
            if (change.Run.Status != RunStatus.Running || change.Run.PauseScope is not null)
                throw new InvalidOperationException("The run cannot request approval in its current state.");
            Move(change, RunStatus.WaitingForApproval, "Waiting for explicit action approval");
            AddEvent(change, RunEventType.ApprovalRequested, "An exact pending action requires owner approval", toolId);
        }, ct, expectedVersion: expectedVersion);

    public Task<TaskRun> EndToolApprovalAsync(Guid runId, long expectedVersion, CancellationToken ct = default) =>
        ChangeAsync(runId, change =>
        {
            if (change.Run.Status != RunStatus.WaitingForApproval || change.Run.PauseScope is not null)
                throw new InvalidOperationException("The pending approval is no longer current.");
            Move(change, RunStatus.Running, "Action approval decision received; policy will be rechecked");
        }, ct, expectedVersion: expectedVersion);

    internal Task RecordProgressAsync(AgentEvent value) => ChangeAsync(value.RunId, change =>
    {
        if (TaskRunStateMachine.IsTerminal(change.Run.Status)) return;
        change.Run = change.Run with { RunVersion = checked(change.Run.RunVersion + 1) };
        AddEvent(change, value.Kind switch
        {
            AgentEventKind.ToolRequested => RunEventType.ToolRequested,
            AgentEventKind.ToolCompleted => RunEventType.ToolExecuted,
            AgentEventKind.WaitingForApproval => RunEventType.ApprovalRequested,
            _ => RunEventType.Progress
        }, value.Message, value.ToolId, value.ToolCallId);
    });
    internal Task FinishExecutionAsync(Guid runId, AgentRunResult result) => ChangeAsync(runId, change =>
    {
        if (TaskRunStateMachine.IsTerminal(change.Run.Status)) return;
        var status = result.Status switch
        {
            AgentResultStatus.Completed => RunStatus.Completed, AgentResultStatus.Cancelled => RunStatus.Cancelled,
            AgentResultStatus.Blocked => RunStatus.Blocked, AgentResultStatus.Failed => RunStatus.Failed,
            _ => throw new InvalidOperationException("Unsupported agent result status.")
        };
        change.Result = result with { Sources = result.Sources.ToArray(), Artifacts = result.Artifacts.ToArray(), Warnings = result.Warnings.ToArray() };
        change.Run = change.Run with { Summary = result.Summary, ErrorCode = result.ErrorCode, Sources = change.Result.Sources };
        Move(change, status, result.Summary);
    });
    internal Task InterruptExecutionAsync(Guid runId) => ChangeAsync(runId, change =>
    {
        if (!TaskRunStateMachine.IsTerminal(change.Run.Status) && change.Run.Status != RunStatus.Queued)
            Move(change, RunStatus.Interrupted, "Application stopped; automatic replay is disabled");
    });
    internal void ReleaseExecution(Guid runId) { lock (sync) Find(runId).Executing = false; }

    public Task WaitAtBoundaryAsync(Guid runId, CancellationToken cancellationToken) =>
        WaitAtBoundaryAsync(runId, RunStepAccess.LocalComputation, cancellationToken);

    public async Task WaitAtBoundaryAsync(Guid runId, RunStepAccess access, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(access);
        while (true)
        {
            await ChangeAsync(runId, change =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (change.Run.Status == RunStatus.Cancelled) throw new OperationCanceledException("Run cancelled.", change.Entry.Cancellation.Token);
                change.Entry.Cancellation.Token.ThrowIfCancellationRequested();
                if (change.Run.Status == RunStatus.Pausing) Move(change, RunStatus.Paused,
                    change.Run.Reason is nameof(PauseReason.AutoPause) or nameof(PauseReason.DesktopEnvironmentChanged) ? change.Run.Reason : "Safe step boundary reached", change.Run.PauseScope);
            }, cancellationToken).ConfigureAwait(false);
            Task? wait;
            lock (sync)
            {
                var entry = Find(runId);
                if (entry.Run.Status == RunStatus.Pausing) continue;
                if (entry.Run.Status == RunStatus.Cancelled) throw new OperationCanceledException("Run cancelled.", entry.Cancellation.Token);
                var guiPauseBlocksStep = entry.Run.PauseScope == PauseScope.GuiOnly &&
                    (access.RequiresDesktop || !access.IsReadOnly || !access.IndependentOfDesktop);
                wait = entry.Run.Status is RunStatus.Paused or RunStatus.PausedForUser || guiPauseBlocksStep ? entry.Resume.Task : null;
            }
            if (wait is null) return;
            await wait.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private Task<TaskRun> Apply<T>(Guid id, Guid runId, long version, T command, Action<Change> apply, CancellationToken ct) =>
        ChangeAsync(runId, apply, ct, id, Fingerprint(command), version);

    private async Task<TaskRun> ChangeAsync(Guid runId, Action<Change> mutate, CancellationToken ct = default,
        Guid? commandId = null, string? fingerprint = null, long? expectedVersion = null)
    {
        await writes.WaitAsync(ct).ConfigureAwait(false);
        Change? change = null;
        TaskRun result;
        try
        {
            lock (sync)
            {
                ObjectDisposedException.ThrowIf(disposed, this);
                if (commandId is { } id && FindReceipt(id, fingerprint!) is { } existing) return existing;
                var entry = Find(runId);
                if (expectedVersion is { } version && entry.Run.RunVersion != version) throw new RunVersionConflictException(runId, version, entry.Run.RunVersion);
                change = new Change(entry); mutate(change);
            }
            if (change.DesktopDrain is not null) await change.DesktopDrain.WaitAsync(ct).ConfigureAwait(false);
            if (change.Cancel && desktopAccess?.Snapshot is { InputCleanupUncertain: true } fault && fault.HumanControlOwnerRunId == runId)
                change.Run = change.Run with { ErrorCode = "InputCleanupUncertain", Reason = "Cancelled; input release unconfirmed; manual review required" };
            if (change.Events.Count > 0 && repository is not null)
                await PersistAsync(() => repository.UpdateAsync(change.Run, change.Entry.Run.RunVersion, change.Events, change.Result, ct), ct).ConfigureAwait(false);
            lock (sync)
            {
                ObjectDisposedException.ThrowIf(disposed, this);
                if (change.ResumeDesktopVersion is { } pauseVersion && desktopAccess?.TryResumeRun(runId, pauseVersion) != true)
                { change.ResumeSuperseded = true; change.Wake = false; }
                Commit(change);
                if (commandId is { } id) receipts.Add(id, (fingerprint!, runId));
                result = change.Entry.Run;
            }
        }
        finally { writes.Release(); }
        if (change.ResumeSuperseded)
            return await ChangeAsync(runId, paused =>
            {
                if (TaskRunStateMachine.IsTerminal(paused.Run.Status)) return;
                _ = desktopAccess?.PauseRunAsync(runId);
                if (paused.Run.PauseScope is null)
                    Move(paused, paused.Run.Status == RunStatus.Queued ? RunStatus.Paused : RunStatus.Pausing,
                        "New user takeover superseded pending resume", PauseScope.EntireRun);
            }, CancellationToken.None).ConfigureAwait(false);
        if (change.Cancel)
        {
            await change.Entry.Cancellation.CancelAsync().ConfigureAwait(false);
            if (desktopAccess is not null) await desktopAccess.CancelRunAsync(runId).ConfigureAwait(false);
        }
        return result;
    }

    private static void Commit(Change change)
    {
        var entry = change.Entry;
        entry.Run = change.Run; entry.Result = change.Result;
        if (change.Executing is { } executing) entry.Executing = executing;
        if (change.NewResume is not null) entry.Resume = change.NewResume;
        entry.Events.AddRange(change.Events);
        foreach (var value in change.Events) foreach (var watcher in entry.Watchers) watcher.Writer.TryWrite(value);
        if (TaskRunStateMachine.IsTerminal(entry.Run.Status))
        {
            foreach (var watcher in entry.Watchers) watcher.Writer.TryComplete();
            entry.Terminal.TrySetResult(entry.Run);
        }
        if (change.Wake) entry.Resume.TrySetResult();
    }
    private static async Task PersistAsync(Func<Task> persist, CancellationToken ct)
    {
        try { await Task.Run(persist, ct).ConfigureAwait(false); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (RunSubmissionConflictException) { throw; }
        catch (ScheduleSubmissionConflictException) { throw; }
        catch (Exception exception) { throw new RunPersistenceException(exception); }
    }
    private TaskRun? FindReceipt(Guid id, string fingerprint)
    {
        if (id == Guid.Empty) throw new ArgumentException("Command ID cannot be empty.");
        if (!receipts.TryGetValue(id, out var receipt)) return null;
        if (receipt.Fingerprint != fingerprint) throw new InvalidOperationException("Command ID was already used with another payload/type.");
        return Find(receipt.RunId).Run;
    }
    private Entry Find(Guid id) => runs.TryGetValue(id, out var entry) ? entry : throw new KeyNotFoundException($"Unknown run {id}.");
    private async Task<AgentContext> ReadConversationContextAsync(Guid? conversationId, CancellationToken ct)
    {
        if (conversationId is not { } id) return new();
        IReadOnlyList<TaskRun> history = repository is null ? GetRuns() : await repository.ListAsync(ct).ConfigureAwait(false);
        var prior = history.Where(run => run.ConversationId == id && run.Status == RunStatus.Completed)
            .OrderBy(run => run.EndedAtUtc).ThenBy(run => run.CreatedAtUtc).TakeLast(4).ToArray();
        var messages = conversations is null ? [] : await conversations.ReadMessagesAsync(id, ct).ConfigureAwait(false);
        // Tool bodies and opaque AgentSessions are intentionally not used for conversation transfer.
        var neutral = messages.Where(message => message.ConversationId == id
            && message.Role is MessageRole.User or MessageRole.Assistant && !string.IsNullOrWhiteSpace(message.Content)).ToList();
        foreach (var run in prior)
        {
            if (!neutral.Any(message => message.RunId == run.Id && message.Role == MessageRole.User))
                neutral.Add(new(run.Id, id, neutral.Count + 1, MessageRole.User, run.Instructions, run.CreatedAtUtc, run.Id));
            if (!string.IsNullOrWhiteSpace(run.Summary) && !neutral.Any(message => message.RunId == run.Id && message.Role == MessageRole.Assistant))
                neutral.Add(new(Guid.NewGuid(), id, neutral.Count + 1, MessageRole.Assistant, run.Summary, run.EndedAtUtc ?? run.CreatedAtUtc, run.Id)
                { Sources = run.Sources, ErrorCode = run.ErrorCode });
        }
        var latest = prior.LastOrDefault();
        return AgentContextWindow.Bound(new(latest?.Summary,
            prior.Reverse().SelectMany(run => run.Sources).Concat(neutral.AsEnumerable().Reverse().SelectMany(message => message.Sources)).ToArray(),
            neutral.OrderBy(message => message.TimestampUtc).ThenBy(message => message.Sequence).ToArray())
        { DecisionContext = AgentContextWindow.Decisions(prior) }, id);
    }
    private static string Fingerprint<T>(T command) => typeof(T).Name + ":" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(command))));
    private static RunSubmissionIdentity SubmissionIdentity(SubmitTaskCommand command)
    {
        // Generated Run/Conversation IDs and the transient CommandId do not change external-trigger intent.
        var intent = JsonSerializer.Serialize(new
        {
            command.Instructions, command.Origin, command.ConversationId, command.ModelProfileId,
            AllowedToolIds = (command.AllowedToolIds ?? []).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray(),
            Limits = command.Limits ?? new RunLimits(), command.ExternalKey
        });
        // Preserve the exact legacy hash when no profile snapshot was provided.
        if (command.Profile is not null) intent += "\nProfile:" + JsonSerializer.Serialize(command.Profile);
        if (command.TaskDefinitionId is { } definitionId) intent += "\nTaskDefinitionId:" + definitionId.ToString("D");
        if (command.SkipReason is not null) intent += "\nSkip:" + command.SkipReason;
        return new RunSubmissionIdentity(command.ExternalKey is null ? "command" : "external/" + command.ExternalKey.Source,
            command.ExternalKey?.Value ?? command.CommandId.ToString("D"), Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(intent))), command.ScheduleClaim);
    }
    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static void Move(Change change, RunStatus status, string message, PauseScope? scope = null)
    {
        var firstStart = status == RunStatus.Running && change.Run.StartedAtUtc is null;
        change.Run = TaskRunStateMachine.Transition(change.Run, status, change.Run.RunVersion, DateTimeOffset.UtcNow, message, scope);
        AddEvent(change, status switch
        {
            RunStatus.Running => firstStart ? RunEventType.RunStarted : RunEventType.RunResumed,
            RunStatus.Paused or RunStatus.PausedForUser => RunEventType.RunPaused,
            _ when TaskRunStateMachine.IsTerminal(status) => RunEventType.RunFinished,
            _ => RunEventType.StatusChanged
        }, message);
        // Persist status with the event, so delayed/restarted notification consumers need not infer
        // an earlier state from the current run or parse human-readable messages.
        change.Events[^1] = change.Events[^1] with { Payload = JsonSerializer.SerializeToElement(new { change.Run.Status, change.Run.RunVersion }) };
    }
    private static void AddEvent(Change change, RunEventType type, string message, string? toolId = null, Guid? callId = null) => change.Events.Add(new RunEvent
    {
        Id = Guid.NewGuid(), RunId = change.Run.Id, Sequence = change.Entry.Events.Count + change.Events.Count + 1,
        EventType = type, TimestampUtc = DateTimeOffset.UtcNow, Message = message, ToolId = toolId, ToolCallId = callId
    });
    public void Dispose()
    {
        Entry[] entries;
        lock (sync)
        {
            if (disposed) return;
            disposed = true; queue.Writer.TryComplete(); entries = runs.Values.ToArray();
            foreach (var entry in entries) foreach (var watcher in entry.Watchers) watcher.Writer.TryComplete();
        }
        foreach (var entry in entries) { entry.Cancellation.Cancel(); entry.Resume.TrySetCanceled(); entry.Cancellation.Dispose(); }
    }
    private sealed class Change(Entry entry)
    {
        public Task? DesktopDrain;
        public long? ResumeDesktopVersion;
        public bool ResumeSuperseded;
        public Entry Entry { get; } = entry;
        public TaskRun Run = entry.Run;
        public AgentRunResult? Result = entry.Result;
        public List<RunEvent> Events { get; } = [];
        public TaskCompletionSource? NewResume;
        public bool? Executing;
        public bool Wake, Cancel;
    }
    private sealed class Entry(TaskRun run, AgentRequest request)
    {
        public TaskRun Run = run;
        public AgentRequest Request { get; } = request;
        public List<RunEvent> Events { get; } = [];
        public List<Channel<RunEvent>> Watchers { get; } = [];
        public CancellationTokenSource Cancellation { get; } = new();
        public TaskCompletionSource Resume = run.PauseScope is null ? CompletedSignal() : Signal();
        public bool Executing;
        public AgentRunResult? Result;
        public TaskCompletionSource<TaskRun> Terminal { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private static TaskCompletionSource CompletedSignal() { var signal = Signal(); signal.SetResult(); return signal; }
    }
}

public sealed class RunPersistenceException(Exception innerException) : InvalidOperationException("Task history could not be saved.", innerException);
