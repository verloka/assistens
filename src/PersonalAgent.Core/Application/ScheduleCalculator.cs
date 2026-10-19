using PersonalAgent.Core.Domain;

namespace PersonalAgent.Core.Application;

public static class ScheduleCalculator
{
    public static TimeZoneInfo ResolveTimeZone(string id)
    {
        if (string.IsNullOrWhiteSpace(id) || id.Length > 128) throw new ArgumentException("Choose a valid time zone.");
        try { return TimeZoneInfo.FindSystemTimeZoneById(id); }
        catch (Exception error) when (error is TimeZoneNotFoundException or InvalidTimeZoneException)
        { throw new ArgumentException("The selected time zone is unavailable."); }
    }
    // Strictly after the supplied UTC instant. Interval starts one period after its immutable creation anchor.
    public static DateTimeOffset? Next(ScheduleRecurrence recurrence, string timeZoneId, DateTimeOffset anchorUtc, DateTimeOffset afterUtc)
    {
        recurrence = recurrence.Snapshot(); var zone = ResolveTimeZone(timeZoneId); afterUtc = afterUtc.ToUniversalTime(); anchorUtc = anchorUtc.ToUniversalTime();
        if (recurrence.Mode == ScheduleMode.Once) return recurrence.OnceAtUtc > afterUtc ? recurrence.OnceAtUtc : null;
        if (recurrence.Mode == ScheduleMode.Interval)
        {
            var period = (long)recurrence.EveryMinutes!.Value * TimeSpan.TicksPerMinute;
            var elapsed = afterUtc.UtcTicks - anchorUtc.UtcTicks;
            var count = elapsed < 0 ? 1 : elapsed / period + 1;
            if (count > (DateTimeOffset.MaxValue.UtcTicks - anchorUtc.UtcTicks) / period) return null;
            return new DateTimeOffset(anchorUtc.UtcTicks + count * period, TimeSpan.Zero);
        }
        var localDate = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(afterUtc, zone).DateTime);
        for (var days = 0; days < 15 && localDate.DayNumber <= DateOnly.MaxValue.DayNumber - days; days++)
        {
            var date = localDate.AddDays(days);
            if (recurrence.Mode == ScheduleMode.Weekdays && !recurrence.Days.Contains(date.DayOfWeek)) continue;
            var local = date.ToDateTime(recurrence.LocalTime!.Value, DateTimeKind.Unspecified);
            if (zone.IsInvalidTime(local)) continue; // Missing wall-clock occurrence is skipped; no guessed time shift.
            var utc = zone.IsAmbiguousTime(local)
                ? new DateTimeOffset(local, zone.GetAmbiguousTimeOffsets(local).Max()).ToUniversalTime() // Earliest instant, once per local date.
                : new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(local, zone));
            if (utc > afterUtc) return utc;
        }
        return null;
    }
}
