using PersonalAgent.Core.Application;
using PersonalAgent.Core.Domain;

namespace PersonalAgent.UnitTests;

public sealed class ScheduleMisfireTests
{
    [Fact]
    public void KyivSpringGapSkipsMissingWallTimeAndFallFoldFiresOnlyEarliestInstant()
    {
        var recurrence = new ScheduleRecurrence { Mode = ScheduleMode.Daily, LocalTime = new(3, 30) };
        var spring = DateTimeOffset.Parse("2026-03-28T01:30:00Z");
        Assert.Equal(DateTimeOffset.Parse("2026-03-30T00:30:00Z"), ScheduleCalculator.Next(recurrence, "Europe/Kyiv", spring, spring));
        var autumn = DateTimeOffset.Parse("2026-10-24T00:30:00Z");
        var first = ScheduleCalculator.Next(recurrence, "Europe/Kyiv", autumn, autumn)!.Value;
        Assert.Equal(DateTimeOffset.Parse("2026-10-25T00:30:00Z"), first);
        Assert.Equal(DateTimeOffset.Parse("2026-10-26T01:30:00Z"), ScheduleCalculator.Next(recurrence, "Europe/Kyiv", autumn, first));
    }

    [Fact]
    public void KyivCalendarPreservesWallClockWhileIntervalsKeepElapsedUtcAcrossDst()
    {
        var recurrence = new ScheduleRecurrence { Mode = ScheduleMode.Daily, LocalTime = new(9, 0) };
        var beforeSpring = DateTimeOffset.Parse("2026-03-28T07:00:00Z");
        Assert.Equal(TimeSpan.FromHours(23), ScheduleCalculator.Next(recurrence, "Europe/Kyiv", beforeSpring, beforeSpring) - beforeSpring);
        var beforeFall = DateTimeOffset.Parse("2026-10-24T06:00:00Z");
        Assert.Equal(TimeSpan.FromHours(25), ScheduleCalculator.Next(recurrence, "Europe/Kyiv", beforeFall, beforeFall) - beforeFall);
        var interval = new ScheduleRecurrence { Mode = ScheduleMode.Interval, EveryMinutes = 60 };
        var anchor = DateTimeOffset.Parse("2026-10-25T00:00:00Z");
        Assert.Equal(anchor.AddHours(1), ScheduleCalculator.Next(interval, "Europe/Kyiv", anchor, anchor));
        Assert.Equal(anchor.AddHours(2), ScheduleCalculator.Next(interval, "Europe/Kyiv", anchor, anchor.AddHours(1)));
    }

    [Fact]
    public void CatchUpConsumesPersistedBatchBudgetThenMovesBeyondNow()
    {
        var anchor = DateTimeOffset.Parse("2026-10-19T08:00:00Z"); var now = anchor.AddHours(5);
        var schedule = new ScheduleDefinition { Id = Guid.NewGuid(), TaskDefinitionId = Guid.NewGuid(), CreatedAtUtc = anchor, UpdatedAtUtc = anchor,
            Recurrence = new() { Mode = ScheduleMode.Interval, EveryMinutes = 60 }, TimeZoneId = "UTC", MisfirePolicy = MisfirePolicy.CatchUp, MaxCatchUpRuns = 3 };
        var first = ScheduleMisfires.Advance(schedule, anchor.AddHours(1), now);
        Assert.Equal(anchor.AddHours(2), first.NextRunAtUtc); Assert.Equal(2, first.CatchUpRemaining); Assert.Equal(now, first.CatchUpUntilUtc);
        schedule = schedule with { CatchUpRemaining = first.CatchUpRemaining, CatchUpUntilUtc = first.CatchUpUntilUtc };
        var second = ScheduleMisfires.Advance(schedule, anchor.AddHours(2), now);
        Assert.Equal(anchor.AddHours(3), second.NextRunAtUtc); Assert.Equal(1, second.CatchUpRemaining);
        schedule = schedule with { CatchUpRemaining = second.CatchUpRemaining, CatchUpUntilUtc = second.CatchUpUntilUtc };
        var last = ScheduleMisfires.Advance(schedule, anchor.AddHours(3), now);
        Assert.Equal(anchor.AddHours(6), last.NextRunAtUtc); Assert.Null(last.CatchUpRemaining); Assert.Null(last.CatchUpUntilUtc);
        Assert.Throws<ArgumentException>(() => ScheduleMisfires.Validate(schedule with { Recurrence = schedule.Recurrence with { EveryMinutes = 10 } }));
    }

    [Fact]
    public void SkipDistinguishesNormalTickJitterFromOfflineMisfireAndDefaultRunOnceDoesNotSkip()
    {
        var now = DateTimeOffset.UtcNow;
        var schedule = new ScheduleDefinition { Id = Guid.NewGuid(), TaskDefinitionId = Guid.NewGuid(), CreatedAtUtc = now, UpdatedAtUtc = now,
            Recurrence = new() { Mode = ScheduleMode.Interval, EveryMinutes = 10 }, TimeZoneId = "UTC" };
        Assert.False(ScheduleMisfires.ShouldSkip(schedule, now.AddDays(-1), now));
        schedule = schedule with { MisfirePolicy = MisfirePolicy.Skip };
        Assert.False(ScheduleMisfires.ShouldSkip(schedule, now, now.AddSeconds(30)));
        Assert.True(ScheduleMisfires.ShouldSkip(schedule, now, now.AddMinutes(2)));
    }
}
