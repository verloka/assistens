using Microsoft.Extensions.Hosting;
using PersonalAgent.Core.Application;
using PersonalAgent.Core.Domain;
using PersonalAgent.Core.Agents;
using System.Globalization;
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
    private string message = "Ready. The selected model will be used for the next task.";
    private TaskRun? selectedHistory;
    private readonly System.Windows.Threading.Dispatcher dispatcher = System.Windows.Threading.Dispatcher.CurrentDispatcher;

    public DashboardViewModel(ITaskCommands commands, TaskRunCoordinator state, IHostApplicationLifetime host, ModelProfilesViewModel? profiles = null,
        TaskToolsViewModel? tools = null)
    {
        this.commands = commands;
        this.state = state;
        state.HistoryContentCleared += OnHistoryContentCleared;
        Profiles = profiles;
        Tools = tools;
        if (Profiles is not null) Profiles.PropertyChanged += ProfileChanged;
        lifetime = CancellationTokenSource.CreateLinkedTokenSource(host.ApplicationStopping);
        SubmitCommand = new AsyncCommand(SubmitAsync, () => !active && !disposed && !lifetime.IsCancellationRequested && !string.IsNullOrWhiteSpace(Instructions));
        PauseCommand = new AsyncCommand(() => ControlAsync(run => commands.PauseAsync(new(Guid.NewGuid(), run.Id, run.RunVersion), lifetime.Token)),
            () => CanControl && CurrentRun!.Status is RunStatus.Queued or RunStatus.Running);
        ResumeCommand = new AsyncCommand(() => ControlAsync(run => commands.ResumeAsync(new(Guid.NewGuid(), run.Id, run.RunVersion), lifetime.Token)),
            () => CanControl && CurrentRun!.Status is RunStatus.Paused or RunStatus.PausedForUser);
        CancelCommand = new AsyncCommand(() => ControlAsync(run => commands.CancelAsync(new(Guid.NewGuid(), run.Id, run.RunVersion), lifetime.Token)), () => CanControl);
        RefreshHistoryCommand = new(() => { RefreshHistory(); return Task.CompletedTask; }, () => !active && !disposed && !lifetime.IsCancellationRequested);
        RefreshHistory();
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    public ModelProfilesViewModel? Profiles { get; }
    public TaskToolsViewModel? Tools { get; }
    public bool ContinueConversation { get; set; }
    public ObservableCollection<RunEvent> Events { get; } = [];
    public ObservableCollection<TaskRun> History { get; } = [];
    public TaskRun? SelectedHistory
    {
        get => selectedHistory;
        set
        {
            if (active || value is null) return;
            Tools?.Reset();
            selectedHistory = value;
            Changed();
            _ = ObserveHistoryAsync(value.Id);
        }
    }
    public AsyncCommand SubmitCommand { get; }
    public AsyncCommand PauseCommand { get; }
    public AsyncCommand ResumeCommand { get; }
    public AsyncCommand CancelCommand { get; }
    public AsyncCommand RefreshHistoryCommand { get; }
    public string Instructions
    {
        get => instructions;
        set { instructions = value; Changed(); SubmitCommand.Refresh(); }
    }
    public TaskRun? CurrentRun => currentRun;
    public string Status => currentRun?.Status.ToString() ?? "Ready";
    public string Summary => currentRun?.Summary ?? currentRun?.Reason ?? "";
    public AgentRunResult? Result => currentRun is null ? null : state.GetResult(currentRun.Id);
    public IReadOnlyList<SourceLink> Sources => Result?.Sources ?? currentRun?.Sources ?? [];
    public string Warnings => string.Join("\n", Result?.Warnings ?? []);
    public string UsageSummary
    {
        get
        {
            if (Result is null) return "";
            if (Result.Usage is not { } usage) return "Token usage: unknown. Cost: unknown.";
            var cost = usage.Cost is { } value ? value.ToString("0.########", CultureInfo.InvariantCulture) + " " + usage.Currency : "unknown";
            return $"Tokens: input {usage.InputTokens}, output {usage.OutputTokens}. Estimated cost: {cost}.";
        }
    }
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
            var profile = Profiles?.Selected?.Profile;
            var selection = Tools?.Snapshot();
            if (selection is not null && profile is null) { message = "Select a configured model profile for GUI/MCP tools. Local demo stays tool-free."; return; }
            Tools?.Reset(); // One deliberate attempt, including a failed/unknown admission, never lends grants to the next task.
            currentRun = await commands.SubmitAsync(new(Guid.NewGuid(), Instructions, new TaskOrigin("desktop"),
                ConversationId: ContinueConversation ? currentRun?.ConversationId : null,
                ModelProfileId: profile?.Id ?? "fake", Profile: profile,
                AllowedToolIds: selection?.Select(rule => rule.ToolId).ToArray(), ToolPermissions: selection), lifetime.Token);
            Refresh();
            await WatchAsync(currentRun.Id);
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { message = "Application is stopping."; }
        catch (RunPersistenceException) { message = "Task history could not be saved; task was not started."; }
        catch (ArgumentException) { message = "Review the tool selection and exact MCP target rules. No task admission was confirmed."; }
        catch (InvalidOperationException) { message = "Review the tool selection. GUI input requires screenshot; MCP mutations require exact target rules. No task admission was confirmed."; }
        catch (Exception) { message = "Task could not be started or observed. Review the current status."; }
        finally
        {
            active = false;
            if (currentRun is not null) currentRun = state.GetRun(currentRun.Id);
            RefreshHistory();
            Refresh();
        }
    }

    private async Task ControlAsync(Func<TaskRun, Task<TaskRun>> command)
    {
        if (currentRun is null) return;
        try { currentRun = await command(state.GetRun(currentRun.Id)); }
        catch (RunVersionConflictException) { message = "Task changed while the command was pending; status refreshed."; }
        catch (RunPersistenceException) { message = "Task history could not be saved; command was not applied."; }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { message = "Application is stopping."; }
        catch (InvalidOperationException) { message = "Command is unavailable in the current task state."; }
        finally { if (currentRun is not null) currentRun = state.GetRun(currentRun.Id); Refresh(); }
    }
    private async Task ObserveHistoryAsync(Guid id)
    {
        active = true; Events.Clear(); currentRun = state.GetRun(id); Refresh();
        try { await WatchAsync(id); }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception) { message = "Task history could not be observed."; }
        finally { active = false; Refresh(); }
    }
    private async Task WatchAsync(Guid id)
    {
        await foreach (var value in state.WatchRunAsync(id, lifetime.Token))
        {
            Events.Add(value); currentRun = state.GetRun(value.RunId); Refresh();
        }
    }

    private void Refresh()
    {
        Changed(nameof(CurrentRun)); Changed(nameof(Status)); Changed(nameof(Summary)); Changed(nameof(Message)); Changed(nameof(IsActive));
        Changed(nameof(Result)); Changed(nameof(Sources)); Changed(nameof(Warnings)); Changed(nameof(UsageSummary));
        SubmitCommand.Refresh(); PauseCommand.Refresh(); ResumeCommand.Refresh(); CancelCommand.Refresh();
        RefreshHistoryCommand.Refresh();
    }
    private void RefreshHistory()
    {
        History.Clear();
        foreach (var run in state.GetRuns()) History.Add(run);
    }
    private void OnHistoryContentCleared()
    {
        void Update()
        {
            if (disposed) return;
            RefreshHistory();
            if (selectedHistory is not null && state.GetRun(selectedHistory.Id).ContentClearedAtUtc is not null)
            {
                selectedHistory = state.GetRun(selectedHistory.Id); Changed(nameof(SelectedHistory));
            }
            if (currentRun is not null && state.GetRun(currentRun.Id).ContentClearedAtUtc is not null)
            {
                currentRun = state.GetRun(currentRun.Id);
                Events.Clear(); foreach (var evt in state.GetEvents(currentRun.Id)) Events.Add(evt);
                Refresh();
            }
        }
        if (dispatcher.CheckAccess()) Update(); else dispatcher.BeginInvoke(Update);
    }
    private void Changed([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new(name));
    private void ProfileChanged(object? sender, PropertyChangedEventArgs args) { if (args.PropertyName == nameof(ModelProfilesViewModel.Selected)) Tools?.Reset(); }
    public void Dispose() { if (disposed) return; disposed = true; state.HistoryContentCleared -= OnHistoryContentCleared; if (Profiles is not null) Profiles.PropertyChanged -= ProfileChanged; lifetime.Cancel(); lifetime.Dispose(); }
}
