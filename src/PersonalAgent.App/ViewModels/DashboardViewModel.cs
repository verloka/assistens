using Microsoft.Extensions.Hosting;
using PersonalAgent.Core.Application;
using PersonalAgent.Core.Domain;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace PersonalAgent.App.ViewModels;

public sealed class DashboardViewModel : INotifyPropertyChanged, IDisposable
{
    private readonly ITaskCommands commands;
    private readonly TaskRunCoordinator state;
    private readonly CancellationTokenSource lifetime;
    private string instructions = "Run a local diagnostic";
    private TaskRun? currentRun;
    private bool active;
    private bool disposed;
    private string message = "Local demo: no model or desktop actions. History clears when the app closes.";

    public DashboardViewModel(ITaskCommands commands, TaskRunCoordinator state, IHostApplicationLifetime host)
    {
        this.commands = commands;
        this.state = state;
        lifetime = CancellationTokenSource.CreateLinkedTokenSource(host.ApplicationStopping);
        SubmitCommand = new AsyncCommand(SubmitAsync, () => !active && !disposed && !lifetime.IsCancellationRequested && !string.IsNullOrWhiteSpace(Instructions));
        PauseCommand = new AsyncCommand(() => ControlAsync(run => commands.PauseAsync(new(Guid.NewGuid(), run.Id, run.RunVersion), lifetime.Token)),
            () => CanControl && CurrentRun!.Status is RunStatus.Queued or RunStatus.Running);
        ResumeCommand = new AsyncCommand(() => ControlAsync(run => commands.ResumeAsync(new(Guid.NewGuid(), run.Id, run.RunVersion), lifetime.Token)),
            () => CanControl && CurrentRun!.Status is RunStatus.Paused or RunStatus.PausedForUser);
        CancelCommand = new AsyncCommand(() => ControlAsync(run => commands.CancelAsync(new(Guid.NewGuid(), run.Id, run.RunVersion), lifetime.Token)), () => CanControl);
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    public ObservableCollection<RunEvent> Events { get; } = [];
    public AsyncCommand SubmitCommand { get; }
    public AsyncCommand PauseCommand { get; }
    public AsyncCommand ResumeCommand { get; }
    public AsyncCommand CancelCommand { get; }
    public string Instructions
    {
        get => instructions;
        set { instructions = value; Changed(); SubmitCommand.Refresh(); }
    }
    public TaskRun? CurrentRun => currentRun;
    public string Status => currentRun?.Status.ToString() ?? "Ready";
    public string Summary => currentRun?.Summary ?? currentRun?.Reason ?? "";
    public string Message => message;
    public bool IsActive => active;
    private bool CanControl => active && !disposed && !lifetime.IsCancellationRequested && currentRun is not null && !TaskRunStateMachine.IsTerminal(currentRun.Status);

    private async Task SubmitAsync()
    {
        active = true;
        Events.Clear();
        Refresh();
        try
        {
            currentRun = await commands.SubmitAsync(new(Guid.NewGuid(), Instructions, new TaskOrigin("desktop")), lifetime.Token);
            Refresh();
            await foreach (var value in state.WatchRunAsync(currentRun.Id, lifetime.Token))
            {
                Events.Add(value);
                currentRun = state.GetRun(value.RunId);
                Refresh();
            }
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { message = "Application is stopping."; }
        catch (Exception) { message = "Task could not be started or observed. Review the current status."; }
        finally
        {
            active = false;
            if (currentRun is not null) currentRun = state.GetRun(currentRun.Id);
            Refresh();
        }
    }

    private async Task ControlAsync(Func<TaskRun, Task<TaskRun>> command)
    {
        if (currentRun is null) return;
        try { currentRun = await command(state.GetRun(currentRun.Id)); }
        catch (RunVersionConflictException) { message = "Task changed while the command was pending; status refreshed."; }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { message = "Application is stopping."; }
        catch (InvalidOperationException) { message = "Command is unavailable in the current task state."; }
        finally { if (currentRun is not null) currentRun = state.GetRun(currentRun.Id); Refresh(); }
    }

    private void Refresh()
    {
        Changed(nameof(CurrentRun)); Changed(nameof(Status)); Changed(nameof(Summary)); Changed(nameof(Message)); Changed(nameof(IsActive));
        SubmitCommand.Refresh(); PauseCommand.Refresh(); ResumeCommand.Refresh(); CancelCommand.Refresh();
    }
    private void Changed([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new(name));
    public void Dispose() { if (disposed) return; disposed = true; lifetime.Cancel(); lifetime.Dispose(); }
}
