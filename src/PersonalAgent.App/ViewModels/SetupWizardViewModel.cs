using Microsoft.Extensions.Hosting;
using PersonalAgent.Core.Application;
using PersonalAgent.Core.Domain;
using System.ComponentModel;

namespace PersonalAgent.App.ViewModels;

public sealed class SetupWizardViewModel : INotifyPropertyChanged, IDisposable
{
    private readonly ITaskCommands commands;
    private readonly TaskRunCoordinator runs;
    private readonly CancellationToken stopping;
    private SubmitTaskCommand? submission;
    private int step;
    private bool busy, disposed;
    private string message = "Choose Local demo for setup without API calls, or configure and select a saved model.";
    public SetupWizardViewModel(ModelProfilesViewModel profiles, ITaskCommands commands, TaskRunCoordinator runs, IHostApplicationLifetime lifetime)
    {
        Profiles = profiles; this.commands = commands; this.runs = runs; stopping = lifetime.ApplicationStopping;
        BackCommand = new(() => { step--; Refresh(); return Task.CompletedTask; }, () => Available && step > 0 && submission is null);
        NextCommand = new(() => { step++; Refresh(); return Task.CompletedTask; }, () => Available && step < 3 && CanProceed);
        TestCommand = new(TestAsync, () => Available && step == 1 && Profiles.Selected?.Profile is not null && Profiles.TestCommand.CanExecute(null));
        RunCommand = new(RunAsync, () => Available && step == 3 && submission is null && CanProceed);
        Profiles.PropertyChanged += ProfileChanged;
    }
    public event PropertyChangedEventHandler? PropertyChanged;
    public ModelProfilesViewModel Profiles { get; }
    public Guid? RunId { get; private set; }
    public int Step => step;
    public string Title => $"Step {step + 1} of 4: " + new[] { "Choose a model", "Check connectivity and capabilities", "Optional connections", "First read-only task" }[step];
    public string Message => message;
    public bool IsModelStep => step == 0;
    public bool IsTestStep => step == 1;
    public bool IsConnectionsStep => step == 2;
    public bool IsRunStep => step == 3;
    public bool Available => !busy && !disposed && !stopping.IsCancellationRequested && Profiles.CanEdit;
    public string Readiness => Profiles.Selected?.Profile is null
        ? "Local demo: no API, vision, tool or network validation. The first task is a local setup check."
        : Profiles.HasUnsavedChanges ? "Save the model settings before proceeding."
        : Profiles.Selected.Profile.Capabilities is not { SupportsImages: true, SupportsToolCalling: true }
            ? "For cloud setup, enable images and tools in Models, save and explicitly test the profile."
            : Profiles.VerifiedProfile == Profiles.Selected.Profile ? "Saved profile connection, image and tool probes confirmed."
            : "Click Test saved profile to validate synthetic image/tool requests. This uses the selected provider API and may incur charges.";
    private bool CanProceed => Profiles.Selected?.Profile is null || !Profiles.HasUnsavedChanges
        && (step == 0 || Profiles.Selected.Profile.Capabilities is { SupportsImages: true, SupportsToolCalling: true }
            && Profiles.VerifiedProfile == Profiles.Selected.Profile);
    public AsyncCommand BackCommand { get; }
    public AsyncCommand NextCommand { get; }
    public AsyncCommand TestCommand { get; }
    public AsyncCommand RunCommand { get; }

    private async Task TestAsync()
    {
        busy = true; Refresh();
        try
        {
            Profiles.TestCommand.Execute(null);
            if (Profiles.TestCommand.ExecutionTask is { } test) await test;
            message = Profiles.VerifiedProfile == Profiles.Selected?.Profile && Profiles.VerifiedProfile is not null
                ? "Saved profile validated. You may continue."
                : "Profile verification did not confirm the required capabilities. Review Models and its safe probe diagnostics.";
        }
        catch (Exception) { message = "Connection test was not confirmed. Review the saved profile."; }
        finally { busy = false; Refresh(); }
    }
    private async Task RunAsync()
    {
        busy = true; Refresh();
        try
        {
            var profile = Profiles.Selected?.Profile;
            submission ??= new(Guid.NewGuid(), "Read-only setup check: reply READY. Do not use tools or perform any external actions.", new("desktop"),
                ModelProfileId: profile?.Id ?? "fake", Profile: profile, AllowedToolIds: [],
                Limits: new() { MaxSteps = 2, MaxTokens = 1024, MaxDuration = TimeSpan.FromSeconds(60) });
            var run = await commands.SubmitAsync(submission, stopping); RunId = run.Id;
            message = "First read-only task submitted. No GUI, MCP or channel tools are granted."; Refresh();
            await foreach (var _ in runs.WatchRunAsync(run.Id, stopping))
            {
                run = runs.GetRun(run.Id);
                if (TaskRunStateMachine.IsTerminal(run.Status) || run.Status == RunStatus.Blocked && runs.GetResult(run.Id) is not null) break;
            }
            message = run.Status == RunStatus.Completed
                ? profile is null ? "Local demo completed. Cloud connectivity, vision and tools were not tested."
                    : "First read-only task completed with the validated saved profile."
                : $"First task status: {run.Status}. Review Run diagnostics; no automatic retry was performed.";
        }
        catch (OperationCanceledException) when (stopping.IsCancellationRequested) { message = "Setup stopped with the application. Review persisted task status on restart."; }
        catch (Exception) { message = "First task could not be confirmed. Review diagnostics; no automatic retry was performed."; }
        finally { busy = false; Refresh(); }
    }
    private void ProfileChanged(object? sender, PropertyChangedEventArgs args) => Refresh();
    public void Refresh()
    {
        PropertyChanged?.Invoke(this, new(null));
        BackCommand.Refresh(); NextCommand.Refresh(); TestCommand.Refresh(); RunCommand.Refresh();
    }
    public void Dispose() { if (disposed) return; disposed = true; Profiles.PropertyChanged -= ProfileChanged; }
}
