using PersonalAgent.Core.Application;
using System.ComponentModel;

namespace PersonalAgent.App.ViewModels;

public sealed class ScheduledDesktopWarningViewModel : INotifyPropertyChanged
{
    private readonly ScheduledDesktopGate gate;
    private readonly Guid requestId;
    private bool deciding;
    public event PropertyChangedEventHandler? PropertyChanged;
    public ScheduledDesktopWarning? Warning { get; private set; }
    public string TaskName => Warning?.TaskName ?? "No pending desktop request";
    public string Policy => Warning?.Policy.ToString() ?? "";
    public string Status => Warning is null ? "Request ended. No desktop permission is granted by this window."
        : Warning.Deferred ? "Deferred. Choose Start now when ready, or skip this occurrence."
        : Warning.SecondsRemaining is null ? "Waiting for this warning to be displayed."
        : $"Countdown: {Warning.SecondsRemaining} seconds. StartWhenIdle waits for idle evidence; AskEveryTime requires Start now.";
    public bool CanDecide => Warning?.Presented == true && !deciding;
    public AsyncCommand StartCommand { get; }
    public AsyncCommand SkipCommand { get; }
    public AsyncCommand DeferCommand { get; }
    public AsyncCommand DisableCommand { get; }

    public ScheduledDesktopWarningViewModel(ScheduledDesktopGate gate, Guid requestId)
    {
        this.gate = gate; this.requestId = requestId;
        StartCommand = Command(ScheduledDesktopDecision.StartNow);
        SkipCommand = Command(ScheduledDesktopDecision.SkipOnce);
        DeferCommand = Command(ScheduledDesktopDecision.Defer);
        DisableCommand = Command(ScheduledDesktopDecision.DisableSchedule);
        Refresh();
    }
    private AsyncCommand Command(ScheduledDesktopDecision decision) => new(() =>
    {
        if (CanDecide && gate.Decide(requestId, decision)) deciding = decision != ScheduledDesktopDecision.Defer;
        Refresh(); return Task.CompletedTask;
    }, () => CanDecide);
    public void Presented() { gate.Present(requestId); Refresh(); }
    public void DeferOnClose() { gate.Decide(requestId, ScheduledDesktopDecision.Defer); Refresh(); }
    public void Refresh()
    {
        Warning = gate.GetWarnings().SingleOrDefault(value => value.Id == requestId);
        PropertyChanged?.Invoke(this, new(""));
        StartCommand.Refresh(); SkipCommand.Refresh(); DeferCommand.Refresh(); DisableCommand.Refresh();
    }
}
