using PersonalAgent.Core.Persistence;

namespace PersonalAgent.Core.Application;

public static class ScheduleOccurrenceIdentity
{
    public const string Source = "schedule";
    public const string PersistedSource = "external/" + Source;
    public static RunTriggerKey Create(Guid scheduleId, DateTimeOffset occurrenceUtc)
    {
        if (scheduleId == Guid.Empty) throw new ArgumentException("A schedule identity is required.");
        return new(Source, Prefix(scheduleId) + occurrenceUtc.UtcTicks.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }
    public static string Prefix(Guid scheduleId) => scheduleId.ToString("N") + "/";
}
public sealed record ScheduleDispatchResult(Guid ScheduleId, DateTimeOffset OccurrenceUtc, Guid? RunId, string? ErrorCode = null);

public sealed class ScheduleDispatcher(IScheduleRepository schedules, ITaskCommands commands,
    IModelProfileRepository? profiles = null, TimeProvider? timeProvider = null)
{
    private readonly TimeProvider clock = timeProvider ?? TimeProvider.System;
    private readonly SemaphoreSlim tick = new(1, 1);
    public async Task<IReadOnlyList<ScheduleDispatchResult>> TickAsync(CancellationToken ct = default)
    {
        await tick.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var now = clock.GetUtcNow(); var results = new List<ScheduleDispatchResult>();
            foreach (var scheduled in await schedules.ListAsync(ct).ConfigureAwait(false))
            {
                if (!scheduled.Task.Enabled || !scheduled.Schedule.Enabled || scheduled.Schedule.NextRunAtUtc is not { } occurrence || occurrence > now) continue;
                Guid? runId = null;
                try
                {
                    runId = await schedules.FindOccurrenceRunAsync(scheduled.Schedule.Id, occurrence, ct).ConfigureAwait(false);
                    if (runId is null)
                    {
                        if (await schedules.HasActiveRunAsync(scheduled.Schedule.Id, ct).ConfigureAwait(false))
                        { results.Add(new(scheduled.Schedule.Id, occurrence, null, "ScheduleRunActive")); continue; }
                        var skip = ScheduleMisfires.ShouldSkip(scheduled.Schedule, occurrence, now);
                        var profile = skip || scheduled.Task.ModelProfileId == "fake" || profiles is null ? null
                            : (await profiles.ListAsync(ct).ConfigureAwait(false)).SingleOrDefault(value => value.Id == scheduled.Task.ModelProfileId);
                        try
                        {
                            var run = await commands.SubmitAsync(new(Guid.NewGuid(), scheduled.Task.Instructions, new("schedule"),
                                ModelProfileId: scheduled.Task.ModelProfileId, Profile: profile,
                                ExternalKey: ScheduleOccurrenceIdentity.Create(scheduled.Schedule.Id, occurrence), TaskDefinitionId: scheduled.Task.Id,
                                ScheduleClaim: new(scheduled.Schedule.Id, scheduled.Schedule.Version, occurrence),
                                SkipReason: skip ? "Scheduled occurrence skipped by its misfire policy." : null), ct).ConfigureAwait(false);
                            runId = run.Id;
                        }
                        catch (RunSubmissionConflictException)
                        {
                            // A competing submit may have committed the original snapshot while configuration changed.
                            runId = await schedules.FindOccurrenceRunAsync(scheduled.Schedule.Id, occurrence, ct).ConfigureAwait(false);
                            if (runId is null) throw;
                        }
                    }
                    var advanced = await schedules.AdvanceAsync(scheduled.Schedule.Id, scheduled.Schedule.Version, occurrence, now, ct).ConfigureAwait(false);
                    results.Add(new(scheduled.Schedule.Id, occurrence, runId, advanced ? null : "ScheduleChanged"));
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (ScheduleSubmissionConflictException) { results.Add(new(scheduled.Schedule.Id, occurrence, null, "ScheduleChanged")); }
                catch (Exception) { results.Add(new(scheduled.Schedule.Id, occurrence, runId, "ScheduleDispatchUnconfirmed")); }
            }
            return results;
        }
        finally { tick.Release(); }
    }
}
