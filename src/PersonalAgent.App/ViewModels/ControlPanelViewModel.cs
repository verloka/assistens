using System.ComponentModel;
using Microsoft.Extensions.Hosting;
using PersonalAgent.Core.Application;
using PersonalAgent.Core.Computer;
using PersonalAgent.Core.Domain;

namespace PersonalAgent.App.ViewModels;

// UI commands only: native input and resource policy stay outside the ViewModel.
public sealed class ControlPanelViewModel
    : INotifyPropertyChanged
{
    private readonly TaskRunCoordinator runs;
    private readonly ITaskCommands commands;
    private readonly IDesktopAccessCoordinator desktop;
    private readonly TakeControlHotkeyService takeControl;
    private readonly CancellationToken stopping;
    private TaskRun? current;
    private bool busy;
    public string Status { get; private set; } = "Idle";
    public string RunTitle => current?.Instructions ?? "No active task";
    public string Progress { get; private set; } = "";
    public string Message { get; private set; } = "";
    public event PropertyChangedEventHandler? PropertyChanged;
    public AsyncCommand TakeControlCommand { get; }
    public AsyncCommand ResumeCommand { get; }
    public AsyncCommand CancelCommand { get; }

    public ControlPanelViewModel(TaskRunCoordinator runs, ITaskCommands commands,
        IDesktopAccessCoordinator desktop, TakeControlHotkeyService takeControl, IHostApplicationLifetime lifetime)
    {
        this.runs = runs; this.commands = commands; this.desktop = desktop; this.takeControl = takeControl;
        stopping = lifetime.ApplicationStopping;
        TakeControlCommand = new(TakeControlAsync, () => !stopping.IsCancellationRequested);
        ResumeCommand = new(() => ControlAsync(false), () => !busy && !stopping.IsCancellationRequested && current is not null &&
            (current.Status is RunStatus.Paused or RunStatus.PausedForUser || current.PauseScope == PauseScope.GuiOnly) &&
            desktop.Snapshot.State != DesktopAccessState.Pausing);
        CancelCommand = new(() => ControlAsync(true), () => !busy && !stopping.IsCancellationRequested && current is not null &&
            !TaskRunStateMachine.IsTerminal(current.Status));
        Refresh();
    }

    public void Refresh()
    {
        var snapshot = desktop.Snapshot;
        var active = runs.GetRuns().Where(run => !TaskRunStateMachine.IsTerminal(run.Status)).ToArray();
        current = snapshot.OwnerRunId is { } owner ? runs.GetRun(owner) :
            active.FirstOrDefault(run => run.Status != RunStatus.Queued) ?? active.FirstOrDefault();
        Status = snapshot.State switch
        {
            DesktopAccessState.Pausing => "Pausing",
            DesktopAccessState.PausedForUser => "Paused",
            _ => current?.Status switch
            {
                RunStatus.Paused or RunStatus.PausedForUser => "Paused",
                RunStatus.Pausing => "Pausing",
                RunStatus.WaitingForApproval => "Approval",
                RunStatus.Blocked => "Error",
                null => "Idle",
                _ => "Running"
            }
        };
        if (Status == "Paused" && string.IsNullOrEmpty(Message))
            Message = "New desktop actions blocked. Explicit Resume required.";
        if (current is not null)
        {
            var events = runs.GetEvents(current.Id);
            var last = events.LastOrDefault(e => e.EventType is RunEventType.Progress or RunEventType.ToolRequested or RunEventType.ToolExecuted);
            Progress = $"Steps: {events.Count(e => e.EventType == RunEventType.Progress)}; {last?.ToolId ?? last?.Message ?? "Waiting"}";
        }
        else Progress = "";
        foreach (var name in new[] { nameof(Status), nameof(RunTitle), nameof(Progress), nameof(Message) })
            PropertyChanged?.Invoke(this, new(name));
        TakeControlCommand.Refresh(); ResumeCommand.Refresh(); CancelCommand.Refresh();
    }

    private async Task TakeControlAsync()
    {
        takeControl.TakeControl(); // Synchronous local gate even while another UI command is pending.
        Message = "New desktop actions blocked. Waiting for the task pause boundary.";
        Refresh();
        if (busy || current is null || current.PauseScope is not null) return;
        busy = true;
        var run = current;
        try { await commands.PauseAsync(new(Guid.NewGuid(), run.Id, run.RunVersion), stopping); }
        catch (Exception error) when (IsCommandFailure(error)) { Message = "Desktop blocked; task pause needs review."; }
        finally { busy = false; Refresh(); }
    }

    private async Task ControlAsync(bool cancel)
    {
        if (current is null) return;
        var run = runs.GetRun(current.Id);
        busy = true; Refresh();
        try
        {
            if (cancel) await commands.CancelAsync(new(Guid.NewGuid(), run.Id, run.RunVersion), stopping);
            else await commands.ResumeAsync(new(Guid.NewGuid(), run.Id, run.RunVersion), stopping);
            Message = cancel ? "Task cancelled. Desktop does not resume automatically." : "Resume requested.";
        }
        catch (Exception error) when (IsCommandFailure(error)) { Message = "Task changed or command could not be saved. Status refreshed; review before retrying."; }
        finally { busy = false; Refresh(); }
    }

    private static bool IsCommandFailure(Exception error) =>
        error is InvalidOperationException or OperationCanceledException or RunPersistenceException;
}
