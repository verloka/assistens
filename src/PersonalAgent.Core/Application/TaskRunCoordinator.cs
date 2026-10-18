using PersonalAgent.Core.Agents;
using PersonalAgent.Core.Domain;
using PersonalAgent.Core.Persistence;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;

namespace PersonalAgent.Core.Application;

// Persist each revision before publishing it. Query locks never cover database I/O.
// Native desktop lease/generation gating remains a separate resource boundary.
public sealed class TaskRunCoordinator(IRunRepository? repository = null) : ITaskCommands, IDisposable
{
    private readonly object sync = new();
    private readonly SemaphoreSlim writes = new(1, 1);
    private readonly Dictionary<Guid, Entry> runs = [];
    private readonly Dictionary<Guid, (string Fingerprint, Guid RunId)> receipts = [];
    private readonly Channel<Guid> queue = Channel.CreateUnbounded<Guid>(new UnboundedChannelOptions { SingleReader = true });
    private bool disposed, runnerAttached;

    public async Task<TaskRun> SubmitAsync(SubmitTaskCommand command, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentException.ThrowIfNullOrWhiteSpace(command.Instructions);
        ArgumentException.ThrowIfNullOrWhiteSpace(command.ModelProfileId);
        ArgumentException.ThrowIfNullOrWhiteSpace(command.Origin.Source);
        (command.Limits ?? new()).Validate();
        if (command.ConversationId == Guid.Empty) throw new ArgumentException("Conversation ID cannot be empty.");
        await writes.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var fingerprint = Fingerprint(command);
            lock (sync)
            {
                ObjectDisposedException.ThrowIf(disposed, this);
                if (FindReceipt(command.CommandId, fingerprint) is { } existing) return existing;
            }
            var now = DateTimeOffset.UtcNow;
            var run = new TaskRun
            {
                Id = Guid.NewGuid(), ConversationId = command.ConversationId ?? Guid.NewGuid(), Origin = command.Origin,
                Instructions = command.Instructions, ModelProfileId = command.ModelProfileId, CreatedAtUtc = now
            };
            var request = new AgentRequest
            {
                RunId = run.Id, ConversationId = run.ConversationId, Instructions = run.Instructions,
                ModelProfileId = run.ModelProfileId, AllowedToolIds = command.AllowedToolIds?.ToArray() ?? [], Limits = command.Limits ?? new()
            };
            var change = new Change(new Entry(run, request));
            AddEvent(change, RunEventType.RunCreated, "Task created");
            Move(change, RunStatus.Queued, "Task queued");
            if (repository is not null)
            {
                var conversation = new Conversation { Id = run.ConversationId, Title = run.Instructions, CreatedAtUtc = now, UpdatedAtUtc = now };
                await PersistAsync(() => repository.CreateAsync(change.Run, conversation, request, change.Events, cancellationToken), cancellationToken).ConfigureAwait(false);
            }
            lock (sync)
            {
                ObjectDisposedException.ThrowIf(disposed, this);
                runs.Add(run.Id, change.Entry);
                Commit(change);
                receipts.Add(command.CommandId, (fingerprint, run.Id));
                queue.Writer.TryWrite(run.Id);
                return change.Entry.Run;
            }
        }
        finally { writes.Release(); }
    }

    public Task<TaskRun> CancelAsync(CancelRunCommand command, CancellationToken cancellationToken = default) =>
        Apply(command.CommandId, command.RunId, command.ExpectedVersion, command, change =>
        {
            if (TaskRunStateMachine.IsTerminal(change.Run.Status)) return;
            change.Run = change.Run with { Summary = null, ErrorCode = null, Sources = [] };
            change.Result = null;
            Move(change, RunStatus.Cancelled, "Cancelled by user");
            change.Cancel = true; change.Wake = true;
        }, cancellationToken);

    public Task<TaskRun> PauseAsync(PauseRunCommand command, CancellationToken cancellationToken = default) =>
        Apply(command.CommandId, command.RunId, command.ExpectedVersion, command, change =>
        {
            if (!Enum.IsDefined(command.Scope)) throw new ArgumentOutOfRangeException(nameof(command.Scope));
            if (TaskRunStateMachine.IsTerminal(change.Run.Status)) throw new InvalidOperationException("Cannot pause a terminal run.");
            if (change.Run.PauseScope is { } scope)
            {
                if (scope != command.Scope) throw new InvalidOperationException("Resume before changing pause scope.");
                return;
            }
            if (command.Scope == PauseScope.GuiOnly)
            {
                change.Run = change.Run with { PauseScope = command.Scope, RunVersion = checked(change.Run.RunVersion + 1) };
                AddEvent(change, RunEventType.DesktopTakenOver, "GUI-only pause requested; no GUI driver is configured");
                return;
            }
            Move(change, change.Run.Status == RunStatus.Queued ? RunStatus.Paused : RunStatus.Pausing, "Pause requested", command.Scope);
            change.NewResume = Signal();
        }, cancellationToken);

    public Task<TaskRun> ResumeAsync(ResumeRunCommand command, CancellationToken cancellationToken = default) =>
        Apply(command.CommandId, command.RunId, command.ExpectedVersion, command, change =>
        {
            if (TaskRunStateMachine.IsTerminal(change.Run.Status)) throw new InvalidOperationException("Cannot resume a terminal run.");
            if (change.Run.Status == RunStatus.Pausing) throw new InvalidOperationException("Wait for the current step to reach the pause boundary.");
            if (change.Run.Status is RunStatus.Paused or RunStatus.PausedForUser)
            {
                Move(change, change.Run.StartedAtUtc is null ? RunStatus.Queued : RunStatus.Running, "Task resumed");
                change.Wake = true;
            }
            else if (change.Run.PauseScope == PauseScope.GuiOnly)
            {
                change.Run = change.Run with { PauseScope = null, RunVersion = checked(change.Run.RunVersion + 1) };
                AddEvent(change, RunEventType.RunResumed, "GUI-only pause cleared; fresh desktop context is still required before GUI use");
            }
            else if (change.Run.Status != RunStatus.Running) throw new InvalidOperationException("Run is not paused.");
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

    public async Task WaitAtBoundaryAsync(Guid runId, CancellationToken cancellationToken)
    {
        while (true)
        {
            await ChangeAsync(runId, change =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (change.Run.Status == RunStatus.Cancelled) throw new OperationCanceledException("Run cancelled.", change.Entry.Cancellation.Token);
                change.Entry.Cancellation.Token.ThrowIfCancellationRequested();
                if (change.Run.Status == RunStatus.Pausing) Move(change, RunStatus.Paused, "Safe step boundary reached", change.Run.PauseScope);
            }, cancellationToken).ConfigureAwait(false);
            Task? wait;
            lock (sync)
            {
                var entry = Find(runId);
                if (entry.Run.Status == RunStatus.Pausing) continue;
                if (entry.Run.Status == RunStatus.Cancelled) throw new OperationCanceledException("Run cancelled.", entry.Cancellation.Token);
                wait = entry.Run.Status is RunStatus.Paused or RunStatus.PausedForUser ? entry.Resume.Task : null;
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
            if (change.Events.Count > 0 && repository is not null)
                await PersistAsync(() => repository.UpdateAsync(change.Run, change.Entry.Run.RunVersion, change.Events, change.Result, ct), ct).ConfigureAwait(false);
            lock (sync)
            {
                ObjectDisposedException.ThrowIf(disposed, this);
                Commit(change);
                if (commandId is { } id) receipts.Add(id, (fingerprint!, runId));
                result = change.Entry.Run;
            }
        }
        finally { writes.Release(); }
        if (change.Cancel) await change.Entry.Cancellation.CancelAsync().ConfigureAwait(false);
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
    private static string Fingerprint<T>(T command) => typeof(T).Name + ":" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(command))));
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
        public TaskCompletionSource Resume = CompletedSignal();
        public bool Executing;
        public AgentRunResult? Result;
        public TaskCompletionSource<TaskRun> Terminal { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private static TaskCompletionSource CompletedSignal() { var signal = Signal(); signal.SetResult(); return signal; }
    }
}

public sealed class RunPersistenceException(Exception innerException) : InvalidOperationException("Task history could not be saved.", innerException);
