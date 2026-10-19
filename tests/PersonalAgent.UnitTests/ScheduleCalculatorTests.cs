using PersonalAgent.Core.Application;
using PersonalAgent.Core.Domain;
using System.Text.Json;

namespace PersonalAgent.UnitTests;

public sealed class ScheduleCalculatorTests
{
    private static readonly DateTimeOffset Anchor = DateTimeOffset.Parse("2026-10-19T08:00:00Z");

    [Fact]
    public void AllFourCadencesReturnStrictlyNextUtcAndIntervalKeepsCreationAnchor()
    {
        var once = new ScheduleRecurrence { Mode = ScheduleMode.Once, OnceAtUtc = Anchor.AddHours(1).ToOffset(TimeSpan.FromHours(3)) };
        Assert.Equal(Anchor.AddHours(1), ScheduleCalculator.Next(once, "UTC", Anchor, Anchor));
        Assert.Null(ScheduleCalculator.Next(once, "UTC", Anchor, Anchor.AddHours(1)));
        var interval = new ScheduleRecurrence { Mode = ScheduleMode.Interval, EveryMinutes = 10 };
        Assert.Equal(Anchor.AddMinutes(10), ScheduleCalculator.Next(interval, "UTC", Anchor, Anchor.AddMinutes(-5)));
        Assert.Equal(Anchor.AddMinutes(30), ScheduleCalculator.Next(interval, "UTC", Anchor, Anchor.AddMinutes(25)));
        var daily = new ScheduleRecurrence { Mode = ScheduleMode.Daily, LocalTime = new(9, 0) };
        Assert.Equal(Anchor.AddHours(1), ScheduleCalculator.Next(daily, "UTC", Anchor, Anchor));
        Assert.Equal(Anchor.AddDays(1).AddHours(1), ScheduleCalculator.Next(daily, "UTC", Anchor, Anchor.AddHours(1)));
        var weekdays = daily with { Mode = ScheduleMode.Weekdays, Days = [DayOfWeek.Monday, DayOfWeek.Wednesday] };
        Assert.Equal(Anchor.AddHours(1), ScheduleCalculator.Next(weekdays, "UTC", Anchor, Anchor));
        Assert.Equal(Anchor.AddDays(2).AddHours(1), ScheduleCalculator.Next(weekdays, "UTC", Anchor, Anchor.AddHours(1)));
    }

    [Fact]
    public void ChosenTimezoneAndSnapshotAreIndependentOfMachineLocalTimeOrMutableDayLists()
    {
        var days = new List<DayOfWeek> { DayOfWeek.Monday };
        var recurrence = new ScheduleRecurrence { Mode = ScheduleMode.Weekdays, Days = days, LocalTime = new(9, 0) }.Snapshot();
        days.Clear();
        var zone = ScheduleCalculator.ResolveTimeZone("Europe/Kyiv");
        var next = ScheduleCalculator.Next(recurrence, zone.Id, Anchor, Anchor)!.Value;
        Assert.Equal(DayOfWeek.Monday, TimeZoneInfo.ConvertTime(next, zone).DayOfWeek);
        Assert.Equal(TimeSpan.FromHours(9), TimeZoneInfo.ConvertTime(next, zone).TimeOfDay);
        Assert.Equal(TimeSpan.Zero, next.Offset); Assert.True(next > Anchor);
        Assert.Equal(next, ScheduleCalculator.Next(ScheduleRecurrence.Parse(JsonSerializer.Serialize(recurrence)), zone.Id, Anchor, Anchor));
        Assert.Throws<ArgumentException>(() => ScheduleCalculator.Next(recurrence, "unknown/synthetic-zone", Anchor, Anchor));
    }

    [Theory]
    [InlineData("")] [InlineData("[]")] [InlineData("{\"Mode\":\"Interval\",\"EveryMinutes\":0}")]
    [InlineData("{\"Mode\":\"Daily\"}")] [InlineData("{\"Mode\":\"Weekdays\",\"LocalTime\":\"09:00:00\",\"Days\":[]}")]
    [InlineData("{\"Mode\":\"Interval\",\"EveryMinutes\":10,\"LocalTime\":\"09:00:00\"}")]
    [InlineData("{\"Mode\":\"Interval\",\"EveryMinutes\":10,\"Unexpected\":true}")]
    [InlineData("{\"Mode\":\"Interval\",\"Mode\":\"Interval\",\"EveryMinutes\":10}")]
    [InlineData("{\"Mode\":\"Weekdays\",\"LocalTime\":\"09:00:00\",\"Days\":[1,1]}")]
    [InlineData("{\"Mode\":\"Interval\",\"EveryMinutes\":10,\"Days\":null}")]
    [InlineData("{\"Mode\":\"Once\",\"OnceAtUtc\":\"2026-10-20T12:00:00\"}")]
    public void ParserRejectsInvalidConflictingUnknownOrAmbiguousConfigurations(string input) =>
        Assert.ThrowsAny<Exception>(() => ScheduleRecurrence.Parse(input));

    [Fact]
    public void ExhaustedDateRangeDoesNotOverflowOrInventAnotherOccurrence()
    {
        Assert.Null(ScheduleCalculator.Next(new() { Mode = ScheduleMode.Interval, EveryMinutes = int.MaxValue }, "UTC", Anchor, DateTimeOffset.MaxValue));
        Assert.Null(ScheduleCalculator.Next(new() { Mode = ScheduleMode.Daily, LocalTime = TimeOnly.MaxValue }, "UTC", Anchor, DateTimeOffset.MaxValue));
    }
}
