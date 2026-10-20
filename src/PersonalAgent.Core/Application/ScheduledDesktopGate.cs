using PersonalAgent.Core.Computer;
using PersonalAgent.Core.Domain;
using PersonalAgent.Core.Persistence;

namespace PersonalAgent.Core.Application;

public enum ScheduledDesktopDecision { StartNow, SkipOnce, Defer, DisableSchedule }
public sealed record ScheduledDesktopWarning(Guid Id, Guid RunId, Guid ScheduleId, string TaskName,
    ScheduledDesktopPolicy Policy, int? SecondsRemaining, bool Deferred, bool Presented);

// Trusted local admission, never a model tool. A warning permits resource acquisition only;
// native session/lease/generation checks and tool permissions remain mandatory.
public sealed class ScheduledDesktopGate(TaskRunCoordinator runs, IScheduleRepository schedules, TimeProvider? timeProvider = null)
{
    private readonly TimeProvider clock = timeProvider ?? TimeProvider.System;
    private readonly object sync = new();
    private readonly Dictionary<Guid, Entry> pending = [];
    private readonly SemaphoreSlim serial = new(1, 1);

    public IReadOnlyList<ScheduledDesktopWarning> GetWarnings()
    {
        lock (sync) return pending.Values.Select(entry => new ScheduledDesktopWarning(entry.Id, entry.Run.Id, entry.Task.Schedule.Id,
            entry.Task.Task.Name, entry.Task.Schedule.DesktopPolicy,
            entry.PresentedAt is { } at ? Math.Max(0, (int)Math.Ceiling(15 - clock.GetElapsedTime(at).TotalSeconds)) : null,
            entry.Deferred, entry.PresentedAt is not null)).ToArray();
    }
    public bool Present(Guid id)
    {
        lock (sync)
        {
            if (!pending.TryGetValue(id, out var entry)) return false;
            entry.PresentedAt ??= clock.GetTimestamp(); return true;
        }
    }
    public bool Decide(Guid id, ScheduledDesktopDecision decision)
    {
        if (!Enum.IsDefined(decision)) return false;
        lock (sync)
        {
            if (!pending.TryGetValue(id, out var entry) || entry.PresentedAt is null || entry.Decision.Task.IsCompleted) return false;
            var current = runs.GetRun(entry.Run.Id);
            if (current.RunVersion != entry.Run.RunVersion || current.Status != RunStatus.WaitingForDesktop || current.PauseScope is not null) return false;
            if (decision == ScheduledDesktopDecision.Defer) { entry.Deferred = true; return true; }
            return entry.Decision.TrySetResult(decision);
        }
    }

    public async Task WaitAsync(Guid runId, CancellationToken ct = default)
    {
        if (runs.GetRun(runId).Origin.Source != ScheduleOccurrenceIdentity.Source) return;
        await serial.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            while (true)
            {
                await runs.WaitAtBoundaryAsync(runId, new(true, false, false), ct).ConfigureAwait(false);
                var current = runs.GetRun(runId);
                if (current.Status != RunStatus.Running || current.PauseScope is not null) throw new DesktopAccessException("ScheduledRunNotActive");
                var task = await schedules.GetForRunAsync(runId, ct).ConfigureAwait(false)
                    ?? throw new DesktopAccessException("ScheduledConfigurationMissing");
                if (!task.Schedule.Enabled || !task.Task.Enabled) throw new DesktopAccessException("ScheduleDisabled");
                TaskRun waiting;
                try { waiting = await runs.BeginScheduledDesktopWaitAsync(runId, current.RunVersion, ct).ConfigureAwait(false); }
                catch (RunVersionConflictException) { continue; }
                var entry = new Entry(waiting, task);
                lock (sync) pending.Add(entry.Id, entry);
                try
                {
                    while (true)
                    {
                        ct.ThrowIfCancellationRequested();
                        current = runs.GetRun(runId);
                        var configuration = await schedules.GetForRunAsync(runId, ct).ConfigureAwait(false);
                        if (current.RunVersion != waiting.RunVersion || current.Status != RunStatus.WaitingForDesktop || current.PauseScope is not null
                            || configuration is null || configuration.Schedule.Version != task.Schedule.Version)
                        {
                            if (current.Status == RunStatus.WaitingForDesktop && current.PauseScope is null)
                                await runs.EndScheduledDesktopWaitAsync(runId, current.RunVersion, false, ct).ConfigureAwait(false);
                            break; // Discard stale consent and start a fresh warning only after explicit resume, if needed.
                        }
                        ScheduledDesktopDecision? choice = null;
                        lock (sync)
                        {
                            if (entry.Decision.Task.IsCompletedSuccessfully) choice = entry.Decision.Task.Result;
                            else if (!entry.Deferred && task.Schedule.DesktopPolicy == ScheduledDesktopPolicy.StartAfterCountdown
                                && entry.PresentedAt is { } at && clock.GetElapsedTime(at) >= TimeSpan.FromSeconds(15)) choice = ScheduledDesktopDecision.StartNow;
                        }
                        if (choice is { } decision)
                        {
                            if (decision == ScheduledDesktopDecision.DisableSchedule)
                            await schedules.SaveAsync(configuration with { Schedule = configuration.Schedule with { Enabled = false } }, task.Schedule.Version, ct).ConfigureAwait(false);
                            var skip = decision is ScheduledDesktopDecision.SkipOnce or ScheduledDesktopDecision.DisableSchedule;
                            await runs.EndScheduledDesktopWaitAsync(runId, waiting.RunVersion, skip, ct).ConfigureAwait(false);
                            if (skip) throw new OperationCanceledException("Scheduled desktop occurrence skipped.", new CancellationToken(true));
                            return;
                        }
                        // Absent presentation/idle evidence never authorizes an unattended desktop grant.
                        try { await entry.Decision.Task.WaitAsync(TimeSpan.FromMilliseconds(250), clock, ct).ConfigureAwait(false); }
                        catch (TimeoutException) { }
                    }
                }
                finally { lock (sync) pending.Remove(entry.Id); }
            }
        }
        finally { serial.Release(); }
    }
    private sealed class Entry(TaskRun run, ScheduledTask task)
    {
        public Guid Id { get; } = Guid.NewGuid();
        public TaskRun Run { get; } = run;
        public ScheduledTask Task { get; } = task;
        public long? PresentedAt;
        public bool Deferred;
        public TaskCompletionSource<ScheduledDesktopDecision> Decision { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
