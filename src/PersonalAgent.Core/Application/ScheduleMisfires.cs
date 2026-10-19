using PersonalAgent.Core.Domain;

namespace PersonalAgent.Core.Application;

public sealed record ScheduleAdvance(DateTimeOffset? NextRunAtUtc, int? CatchUpRemaining = null, DateTimeOffset? CatchUpUntilUtc = null);
public static class ScheduleMisfires
{
    public static TimeSpan JitterGrace { get; } = TimeSpan.FromMinutes(1);
    public static void Validate(ScheduleDefinition schedule)
    {
        if (!Enum.IsDefined(schedule.MisfirePolicy) || schedule.MaxCatchUpRuns is < 1 or > 10
            || schedule.MisfirePolicy == MisfirePolicy.CatchUp && schedule.Recurrence.Mode == ScheduleMode.Interval && schedule.Recurrence.EveryMinutes < 60)
            throw new ArgumentException("CatchUp requires at most ten runs and an interval of at least one hour or a calendar recurrence.");
    }
    public static bool ShouldSkip(ScheduleDefinition schedule, DateTimeOffset due, DateTimeOffset now) =>
        schedule.MisfirePolicy == MisfirePolicy.Skip && now - due > JitterGrace;
    public static ScheduleAdvance Advance(ScheduleDefinition schedule, DateTimeOffset occurrence, DateTimeOffset now)
    {
        Validate(schedule);
        DateTimeOffset? Next(DateTimeOffset after) => ScheduleCalculator.Next(schedule.Recurrence, schedule.TimeZoneId, schedule.CreatedAtUtc, after);
        if (schedule.MisfirePolicy != MisfirePolicy.CatchUp || schedule.CatchUpUntilUtc is null && now - occurrence <= JitterGrace)
            return new(Next(now));
        var remaining = schedule.CatchUpRemaining is { } prior ? prior - 1 : schedule.MaxCatchUpRuns - 1;
        var until = schedule.CatchUpUntilUtc ?? now;
        var candidate = Next(occurrence);
        return remaining > 0 && candidate is { } next && next <= until ? new(next, remaining, until) : new(Next(now));
    }
}
