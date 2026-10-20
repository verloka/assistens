namespace PersonalAgent.Core.Computer;

// Session input aggregate, separate from agent progress. Includes injected input;
// this is a pre-acquisition signal, not an in-run human-activity detector.
public sealed record SystemIdleSnapshot(bool DesktopAvailable, TimeSpan? InputIdleTime);
public interface ISystemIdleSource
{
    SystemIdleSnapshot Read();
}

public sealed record ScheduledDesktopOptions
{
    public TimeSpan IdleThreshold { get; init; } = TimeSpan.FromSeconds(60);
    public void Validate()
    {
        if (IdleThreshold <= TimeSpan.Zero || IdleThreshold > TimeSpan.FromHours(24)) throw new ArgumentOutOfRangeException(nameof(IdleThreshold));
    }
}
