using PersonalAgent.Core.Computer;
using PersonalAgent.Core.Domain;
using PersonalAgent.Core.Persistence;

namespace PersonalAgent.Core.Application;

public enum ScheduledDesktopDecision { StartNow, SkipOnce, Defer, DisableSchedule }
public sealed record ScheduledDesktopWarning(Guid Id, Guid RunId, Guid ScheduleId, string TaskName,
    ScheduledDesktopPolicy Policy, int? SecondsRemaining, bool Deferred, bool Presented,
    bool DesktopAvailable = false, TimeSpan? InputIdleTime = null, TimeSpan? IdleThreshold = null,
    TimeSpan? WaitRemaining = null, bool AcceptingDecisions = true);

// Trusted local admission, never a model tool. A warning permits resource acquisition only;
// native session/lease/generation checks and tool permissions remain mandatory.
public sealed class ScheduledDesktopGate(TaskRunCoordinator runs, IScheduleRepository schedules, TimeProvider? timeProvider = null,
    ISystemIdleSource? systemIdle = null, ScheduledDesktopOptions? options = null)
{
    private readonly TimeProvider clock = timeProvider ?? TimeProvider.System;
    private readonly ScheduledDesktopOptions settings = options ?? new();
    private readonly object sync = new();
    private readonly Dictionary<Guid, Entry> pending = [];
    private readonly SemaphoreSlim serial = new(1, 1);

    public IReadOnlyList<ScheduledDesktopWarning> GetWarnings()
    {
        lock (sync) return pending.Values.Select(entry => new ScheduledDesktopWarning(entry.Id, entry.Run.Id, entry.Task.Schedule.Id,
            entry.Task.Task.Name, entry.Task.Schedule.DesktopPolicy,
            entry.PresentedAt is { } at ? Math.Max(0, (int)Math.Ceiling(15 - clock.GetElapsedTime(at).TotalSeconds)) : null,
            entry.Deferred, entry.PresentedAt is not null, entry.Idle.DesktopAvailable, entry.Idle.InputIdleTime, settings.IdleThreshold,
            Remaining(entry.StartedAt), !entry.Closing)).ToArray();
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
        if (decision == ScheduledDesktopDecision.StartNow && !ReadIdle().DesktopAvailable) return false;
        lock (sync)
        {
            if (!pending.TryGetValue(id, out var entry) || entry.Closing || entry.PresentedAt is null || entry.Decision.Task.IsCompleted) return false;
            var current = runs.GetRun(entry.Run.Id);
            if (current.RunVersion != entry.Run.RunVersion || current.Status != RunStatus.WaitingForDesktop || current.PauseScope is not null) return false;
            if (decision == ScheduledDesktopDecision.Defer) { entry.Deferred = true; return true; }
            return entry.Decision.TrySetResult(decision);
        }
    }

    public async Task WaitAsync(Guid runId, CancellationToken ct = default)
    {
        if (runs.GetRun(runId).Origin.Source != ScheduleOccurrenceIdentity.Source) return;
        settings.Validate();
        await serial.WaitAsync(ct).ConfigureAwait(false);
        var waitStarted = clock.GetTimestamp();
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
                var entry = new Entry(waiting, task, waitStarted) { Idle = ReadIdle() };
                if (entry.Idle.InputIdleTime is { } idleTime && idleTime < settings.IdleThreshold)
                    Diagnostics.AgentTelemetry.Record(Diagnostics.AgentOperation.GuiBusyAdmission, Diagnostics.AgentOperationOutcome.Blocked);
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
                        var automatic = false;
                        var idle = ReadIdle();
                        var expired = Remaining(waitStarted) == TimeSpan.Zero;
                        lock (sync)
                        {
                            entry.Idle = idle;
                            if (entry.Decision.Task.IsCompletedSuccessfully) choice = entry.Decision.Task.Result;
                            if (expired)
                            {
                                choice = choice == ScheduledDesktopDecision.DisableSchedule ? choice : ScheduledDesktopDecision.SkipOnce;
                                entry.Closing = true; // Decisions after the TTL winner are refused, never reported as accepted and ignored.
                            }
                            else if (choice is null && !entry.Deferred && idle.DesktopAvailable
                                && (task.Schedule.DesktopPolicy == ScheduledDesktopPolicy.StartAfterCountdown
                                    || task.Schedule.DesktopPolicy == ScheduledDesktopPolicy.StartWhenIdle && IsIdle(idle))
                                && entry.PresentedAt is { } at && clock.GetElapsedTime(at) >= TimeSpan.FromSeconds(15))
                            { choice = ScheduledDesktopDecision.StartNow; automatic = true; }
                        }
                        if (choice is { } decision)
                        {
                            if (decision == ScheduledDesktopDecision.StartNow && !idle.DesktopAvailable)
                            {
                                // Availability changed after the click; discard that permission, never retain it until unlock.
                                await runs.EndScheduledDesktopWaitAsync(runId, waiting.RunVersion, false, ct).ConfigureAwait(false);
                                break;
                            }
                            var observed = clock.GetUtcNow();
                            if (task.Schedule.MisfirePolicy == MisfirePolicy.RunOnce && task.Schedule.NextRunAtUtc is { } due && due <= observed)
                            {
                                var coalesced = await schedules.CoalescePendingAsync(runId, task.Schedule.Version, observed, ct).ConfigureAwait(false);
                                if (coalesced is null)
                                {
                                    await runs.EndScheduledDesktopWaitAsync(runId, waiting.RunVersion, false, ct).ConfigureAwait(false);
                                    break;
                                }
                                task = configuration = coalesced;
                                lock (sync) entry.Task = task;
                            }
                            if (decision == ScheduledDesktopDecision.DisableSchedule)
                                await schedules.SaveAsync(configuration with { Schedule = configuration.Schedule with { Enabled = false } }, task.Schedule.Version, ct).ConfigureAwait(false);
                            var skip = decision is ScheduledDesktopDecision.SkipOnce or ScheduledDesktopDecision.DisableSchedule;
                            var continued = await runs.EndScheduledDesktopWaitAsync(runId, waiting.RunVersion, skip, ct, expired).ConfigureAwait(false);
                            if (skip) throw new OperationCanceledException("Scheduled desktop occurrence skipped.", new CancellationToken(true));
                            bool veto;
                            lock (sync)
                            {
                                veto = automatic && (entry.Deferred || entry.Decision.Task.IsCompletedSuccessfully && entry.Decision.Task.Result != ScheduledDesktopDecision.StartNow);
                                if (entry.Decision.Task.IsCompletedSuccessfully && entry.Decision.Task.Result == ScheduledDesktopDecision.StartNow) automatic = false;
                            }
                            if (veto || Remaining(waitStarted) == TimeSpan.Zero)
                            {
                                // Honor a locally accepted Skip/Defer/Disable received while the admission write was awaiting storage.
                                waiting = await runs.BeginScheduledDesktopWaitAsync(runId, continued.RunVersion, ct).ConfigureAwait(false);
                                lock (sync) entry.Run = waiting;
                                continue;
                            }
                            var finalConfiguration = await schedules.GetForRunAsync(runId, ct).ConfigureAwait(false);
                            var finalIdle = ReadIdle();
                            if (Remaining(waitStarted) == TimeSpan.Zero)
                            {
                                waiting = await runs.BeginScheduledDesktopWaitAsync(runId, continued.RunVersion, ct).ConfigureAwait(false);
                                lock (sync) entry.Run = waiting;
                                continue;
                            }
                            if (finalConfiguration?.Schedule.Version != task.Schedule.Version || !finalIdle.DesktopAvailable
                                || automatic && task.Schedule.DesktopPolicy == ScheduledDesktopPolicy.StartWhenIdle && !IsIdle(finalIdle))
                                break; // Input/lock during the awaited store write requires a new presented warning.
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
    private bool IsIdle(SystemIdleSnapshot idle) => idle.InputIdleTime is { } elapsed && elapsed >= settings.IdleThreshold;
    private TimeSpan Remaining(long started)
    {
        var remaining = settings.MaxDesktopWait - clock.GetElapsedTime(started);
        return remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero;
    }
    private SystemIdleSnapshot ReadIdle()
    {
        try
        {
            var value = systemIdle?.Read();
            return value is null || value.InputIdleTime < TimeSpan.Zero ? new(false, null) : value;
        }
        catch (Exception) { return new(false, null); }
    }
    private sealed class Entry(TaskRun run, ScheduledTask task, long startedAt)
    {
        public Guid Id { get; } = Guid.NewGuid();
        public TaskRun Run { get; set; } = run;
        public ScheduledTask Task { get; set; } = task;
        public long StartedAt { get; } = startedAt;
        public long? PresentedAt;
        public bool Deferred;
        public bool Closing;
        public SystemIdleSnapshot Idle = new(false, null);
        public TaskCompletionSource<ScheduledDesktopDecision> Decision { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
