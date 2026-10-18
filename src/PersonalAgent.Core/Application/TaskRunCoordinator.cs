using PersonalAgent.Core.Agents;
using PersonalAgent.Core.Domain;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using System.Runtime.CompilerServices;

namespace PersonalAgent.Core.Application;

// Session-local state owner. Durable storage/command deduplication is later backlog scope.
// No GUI driver is exposed here; desktop generation/lease gating remains separate.
public sealed class TaskRunCoordinator : ITaskCommands, IDisposable
{
    private readonly object sync = new();
    private readonly Dictionary<Guid, Entry> runs = [];
    private readonly Dictionary<Guid, (string Fingerprint, Guid RunId)> receipts = [];
    private readonly Channel<Guid> queue = Channel.CreateUnbounded<Guid>(new UnboundedChannelOptions { SingleReader = true });
    private bool disposed;
    private bool runnerAttached;

    public Task<TaskRun> SubmitAsync(SubmitTaskCommand command, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentException.ThrowIfNullOrWhiteSpace(command.Instructions);
        ArgumentException.ThrowIfNullOrWhiteSpace(command.ModelProfileId);
        ArgumentException.ThrowIfNullOrWhiteSpace(command.Origin.Source);
        (command.Limits ?? new()).Validate();
        if (command.ConversationId == Guid.Empty) throw new ArgumentException("Conversation ID cannot be empty.");
        lock (sync)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            var fingerprint = Fingerprint(command);
            if (FindReceipt(command.CommandId, fingerprint) is { } existing) return Task.FromResult(existing);
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
            var entry = new Entry(run, request);
            runs.Add(run.Id, entry);
            AddEvent(entry, RunEventType.RunCreated, "Task created");
            Move(entry, RunStatus.Queued, "Task queued");
            receipts.Add(command.CommandId, (fingerprint, run.Id));
            queue.Writer.TryWrite(run.Id);
            return Task.FromResult(entry.Run);
        }
    }

    public async Task<TaskRun> CancelAsync(CancelRunCommand command, CancellationToken cancellationToken = default)
    {
        CancellationTokenSource? cancellation = null;
        var run = await Apply(command.CommandId, command.RunId, command.ExpectedVersion, command, entry =>
        {
            if (TaskRunStateMachine.IsTerminal(entry.Run.Status)) return;
            Move(entry, RunStatus.Cancelled, "Cancelled by user");
            entry.Resume.TrySetResult();
            cancellation = entry.Cancellation;
        }, cancellationToken);
        // Cancellation callbacks may belong to adapters; never invoke them under the state lock.
        if (cancellation is not null) await cancellation.CancelAsync();
        return run;
    }

    public Task<TaskRun> PauseAsync(PauseRunCommand command, CancellationToken cancellationToken = default) =>
        Apply(command.CommandId, command.RunId, command.ExpectedVersion, command, entry =>
        {
            if (!Enum.IsDefined(command.Scope)) throw new ArgumentOutOfRangeException(nameof(command.Scope));
            if (TaskRunStateMachine.IsTerminal(entry.Run.Status)) throw new InvalidOperationException("Cannot pause a terminal run.");
            if (entry.Run.PauseScope is { } scope)
            {
                if (scope != command.Scope) throw new InvalidOperationException("Resume before changing pause scope.");
                return;
            }
            if (command.Scope == PauseScope.GuiOnly)
            {
                // GUI gate metadata only: independent non-GUI work may continue.
                entry.Run = entry.Run with { PauseScope = command.Scope, RunVersion = checked(entry.Run.RunVersion + 1) };
                AddEvent(entry, RunEventType.DesktopTakenOver, "GUI-only pause requested; no GUI driver is configured");
                return;
            }
            if (entry.Run.Status == RunStatus.Queued) Move(entry, RunStatus.WaitingForUser, "Queued task paused before execution");
            Move(entry, RunStatus.Pausing, "Pause requested", command.Scope);
            entry.Resume = new(TaskCreationOptions.RunContinuationsAsynchronously);
            // Before execution there is no current atomic step to wait for.
            if (!entry.Executing) Move(entry, RunStatus.Paused, "Task paused", command.Scope);
        }, cancellationToken);

    public Task<TaskRun> ResumeAsync(ResumeRunCommand command, CancellationToken cancellationToken = default) =>
        Apply(command.CommandId, command.RunId, command.ExpectedVersion, command, entry =>
        {
            if (TaskRunStateMachine.IsTerminal(entry.Run.Status)) throw new InvalidOperationException("Cannot resume a terminal run.");
            if (entry.Run.Status == RunStatus.Pausing) throw new InvalidOperationException("Wait for the current step to reach the pause boundary.");
            if (entry.Run.Status is RunStatus.Paused or RunStatus.PausedForUser)
            {
                Move(entry, entry.Run.StartedAtUtc is null ? RunStatus.Queued : RunStatus.Running, "Task resumed");
                entry.Resume.TrySetResult();
                return;
            }
            if (entry.Run.PauseScope == PauseScope.GuiOnly)
            {
                entry.Run = entry.Run with { PauseScope = null, RunVersion = checked(entry.Run.RunVersion + 1) };
                AddEvent(entry, RunEventType.RunResumed, "GUI-only pause cleared; fresh desktop context is still required before GUI use");
                return;
            }
            if (entry.Run.Status != RunStatus.Running) throw new InvalidOperationException("Run is not paused.");
        }, cancellationToken);

    public TaskRun GetRun(Guid runId) { lock (sync) return Find(runId).Run; }
    public IReadOnlyList<RunEvent> GetEvents(Guid runId) { lock (sync) return Find(runId).Events.ToArray(); }
    public AgentRunResult? GetResult(Guid runId) { lock (sync) return Find(runId).Result; }
    public Task<TaskRun> WaitForTerminalAsync(Guid runId, CancellationToken cancellationToken = default)
    {
        lock (sync) return Find(runId).Terminal.Task.WaitAsync(cancellationToken);
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
        try
        {
            await foreach (var value in updates.Reader.ReadAllAsync(cancellationToken)) yield return value;
        }
        finally { lock (sync) Find(runId).Watchers.Remove(updates); }
    }

    internal ChannelReader<Guid> AttachRunner()
    {
        lock (sync)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (runnerAttached) throw new InvalidOperationException("A runner is already attached.");
            runnerAttached = true;
            return queue.Reader;
        }
    }
    internal void DetachRunner() { lock (sync) runnerAttached = false; }
    internal (AgentRequest Request, CancellationToken Cancellation) GetExecution(Guid runId)
    {
        lock (sync) { var entry = Find(runId); return (entry.Request, entry.Cancellation.Token); }
    }
    internal async Task<bool> StartExecutionAsync(Guid runId, CancellationToken cancellationToken)
    {
        while (true)
        {
            await WaitAtBoundaryAsync(runId, cancellationToken);
            lock (sync)
            {
                var entry = Find(runId);
                cancellationToken.ThrowIfCancellationRequested();
                if (TaskRunStateMachine.IsTerminal(entry.Run.Status) || entry.Executing) return false;
                if (entry.Run.Status is RunStatus.Paused or RunStatus.Pausing or RunStatus.PausedForUser) continue;
                if (entry.Run.Status != RunStatus.Queued) return false;
                entry.Executing = true;
                Move(entry, RunStatus.Running, "Task started");
                return true;
            }
        }
    }
    internal void RecordProgress(AgentEvent value)
    {
        lock (sync)
        {
            var entry = Find(value.RunId);
            if (TaskRunStateMachine.IsTerminal(entry.Run.Status)) return;
            AddEvent(entry, value.Kind switch
            {
                AgentEventKind.ToolRequested => RunEventType.ToolRequested,
                AgentEventKind.ToolCompleted => RunEventType.ToolExecuted,
                AgentEventKind.WaitingForApproval => RunEventType.ApprovalRequested,
                _ => RunEventType.Progress
            }, value.Message, value.ToolId, value.ToolCallId);
        }
    }
    internal void FinishExecution(Guid runId, AgentRunResult result)
    {
        lock (sync)
        {
            var entry = Find(runId);
            if (TaskRunStateMachine.IsTerminal(entry.Run.Status)) return;
            var status = result.Status switch
            {
                AgentResultStatus.Completed => RunStatus.Completed,
                AgentResultStatus.Cancelled => RunStatus.Cancelled,
                AgentResultStatus.Blocked => RunStatus.Blocked,
                AgentResultStatus.Failed => RunStatus.Failed,
                _ => throw new InvalidOperationException("Unsupported agent result status.")
            };
            entry.Result = result with { Sources = result.Sources.ToArray(), Artifacts = result.Artifacts.ToArray(), Warnings = result.Warnings.ToArray() };
            entry.Run = entry.Run with { Summary = result.Summary, ErrorCode = result.ErrorCode, Sources = entry.Result.Sources };
            Move(entry, status, result.Summary);
        }
    }
    internal void InterruptExecution(Guid runId)
    {
        lock (sync)
        {
            var entry = Find(runId);
            if (!TaskRunStateMachine.IsTerminal(entry.Run.Status) && entry.Run.Status != RunStatus.Queued)
                Move(entry, RunStatus.Interrupted, "Application stopped; automatic replay is disabled");
        }
    }
    internal void ReleaseExecution(Guid runId) { lock (sync) Find(runId).Executing = false; }

    // Used by adapters before the next independent step, never as a desktop lease.
    public async Task WaitAtBoundaryAsync(Guid runId, CancellationToken cancellationToken)
    {
        while (true)
        {
            Task wait;
            lock (sync)
            {
                var entry = Find(runId);
                cancellationToken.ThrowIfCancellationRequested();
                if (entry.Run.Status == RunStatus.Cancelled) throw new OperationCanceledException("Run cancelled.", entry.Cancellation.Token);
                entry.Cancellation.Token.ThrowIfCancellationRequested();
                if (entry.Run.Status == RunStatus.Pausing) Move(entry, RunStatus.Paused, "Safe step boundary reached", entry.Run.PauseScope);
                if (entry.Run.Status is not (RunStatus.Paused or RunStatus.PausedForUser)) return;
                wait = entry.Resume.Task;
            }
            await wait.WaitAsync(cancellationToken);
        }
    }

    public void Dispose()
    {
        Entry[] entries;
        lock (sync)
        {
            if (disposed) return;
            disposed = true;
            queue.Writer.TryComplete();
            entries = runs.Values.ToArray();
            foreach (var entry in entries)
                foreach (var watcher in entry.Watchers) watcher.Writer.TryComplete();
        }
        foreach (var entry in entries)
        {
            entry.Cancellation.Cancel();
            entry.Resume.TrySetCanceled();
            entry.Cancellation.Dispose();
        }
    }

    private Task<TaskRun> Apply<T>(Guid commandId, Guid runId, long expectedVersion, T command, Action<Entry> apply, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        lock (sync)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            var fingerprint = Fingerprint(command);
            if (FindReceipt(commandId, fingerprint) is { } existing) return Task.FromResult(existing);
            var entry = Find(runId);
            if (entry.Run.RunVersion != expectedVersion) throw new RunVersionConflictException(runId, expectedVersion, entry.Run.RunVersion);
            apply(entry);
            receipts.Add(commandId, (fingerprint, runId));
            return Task.FromResult(entry.Run);
        }
    }

    private TaskRun? FindReceipt(Guid commandId, string fingerprint)
    {
        if (commandId == Guid.Empty) throw new ArgumentException("Command ID cannot be empty.");
        if (!receipts.TryGetValue(commandId, out var receipt)) return null;
        if (receipt.Fingerprint != fingerprint) throw new InvalidOperationException("Command ID was already used with another payload/type.");
        return Find(receipt.RunId).Run;
    }

    private Entry Find(Guid runId) => runs.TryGetValue(runId, out var entry) ? entry : throw new KeyNotFoundException($"Unknown run {runId}.");
    private static string Fingerprint<T>(T command) => typeof(T).Name + ":" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(command))));

    private static void Move(Entry entry, RunStatus status, string message, PauseScope? scope = null)
    {
        var firstStart = status == RunStatus.Running && entry.Run.StartedAtUtc is null;
        entry.Run = TaskRunStateMachine.Transition(entry.Run, status, entry.Run.RunVersion, DateTimeOffset.UtcNow, message, scope);
        var type = status switch
        {
            RunStatus.Running => firstStart ? RunEventType.RunStarted : RunEventType.RunResumed,
            RunStatus.Paused or RunStatus.PausedForUser => RunEventType.RunPaused,
            _ when TaskRunStateMachine.IsTerminal(status) => RunEventType.RunFinished,
            _ => RunEventType.StatusChanged
        };
        AddEvent(entry, type, message);
        if (TaskRunStateMachine.IsTerminal(status)) entry.Terminal.TrySetResult(entry.Run);
    }

    private static void AddEvent(Entry entry, RunEventType type, string message, string? toolId = null, Guid? toolCallId = null)
    {
        var value = new RunEvent
        {
            Id = Guid.NewGuid(), RunId = entry.Run.Id, Sequence = entry.Events.Count + 1,
            EventType = type, TimestampUtc = DateTimeOffset.UtcNow, Message = message, ToolId = toolId, ToolCallId = toolCallId
        };
        entry.Events.Add(value);
        foreach (var watcher in entry.Watchers)
        {
            watcher.Writer.TryWrite(value);
            if (TaskRunStateMachine.IsTerminal(entry.Run.Status)) watcher.Writer.TryComplete();
        }
    }

    private sealed class Entry(TaskRun run, AgentRequest request)
    {
        public TaskRun Run = run;
        public AgentRequest Request { get; } = request;
        public List<RunEvent> Events { get; } = [];
        public List<Channel<RunEvent>> Watchers { get; } = [];
        public CancellationTokenSource Cancellation { get; } = new();
        public TaskCompletionSource Resume = CompletedSignal();
        public bool Executing = false;
        public AgentRunResult? Result;
        public TaskCompletionSource<TaskRun> Terminal { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private static TaskCompletionSource CompletedSignal() { var signal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); signal.SetResult(); return signal; }
    }
}
