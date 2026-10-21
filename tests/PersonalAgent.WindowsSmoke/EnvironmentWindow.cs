using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using PersonalAgent.App;
using PersonalAgent.Core.Application;
using PersonalAgent.Core.Computer;
using PersonalAgent.Core.Domain;
using PersonalAgent.Persistence;
using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;

namespace PersonalAgent.WindowsSmoke;

// Real Windows event delivery + production pause/checkpoint, with zero native input or capture.
internal sealed class EnvironmentWindow : Window
{
    private readonly ComboBox mode = new() { ItemsSource = new[] { "Lock/unlock", "Sleep/wake", "Display change" }, SelectedIndex = 0 };
    private readonly Button arm = new() { Content = "ARM selected cycle (no input/capture)", Margin = new(0, 10, 0, 10) };
    private readonly Button check = new() { Content = "CHECK after the cycle", IsEnabled = false };
    private readonly TextBox report = new() { IsReadOnly = true, AcceptsReturn = true, Height = 240, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    private readonly string directory = Path.Combine(Path.GetTempPath(), "PersonalAgent.Environment", Guid.NewGuid().ToString("N"));
    private readonly DesktopAccessCoordinator desktop = new(_ => true);
    private TaskRunCoordinator? runs;
    private SqliteRunRepository? repository;
    private TakeControlHotkeyService? takeover;
    private DesktopEnvironmentPauseService? environment;
    private DesktopLease? lease;
    private Guid? runId;
    private long[] before = [];
    private string selected = "";
    private Task? arming, checking;
    private bool closing, closed;

    public EnvironmentWindow()
    {
        Title = "PersonalAgent attended environment events (PA-113)"; Width = 850; Height = 470; ShowActivated = false;
        var panel = new StackPanel { Margin = new(20) };
        panel.Children.Add(new TextBlock { TextWrapping = TextWrapping.Wrap, Text =
            "Read-only Windows events; no input, screenshots, clipboard, LLM or credentials. Opening does not subscribe. " +
            "Choose a cycle and ARM. Perform it yourself: lock/unlock Windows, sleep/wake, or change/restore display resolution. " +
            "After returning, CHECK and copy the report. ARM the next cycle only after OK. The temporary database contains only synthetic test metadata." });
        panel.Children.Add(mode); panel.Children.Add(arm); panel.Children.Add(check); panel.Children.Add(report); Content = panel;
        arm.Click += (_, _) => arming = ArmAsync(); check.Click += (_, _) => checking = CheckAsync(); Closing += CloseAsync;
    }
    private async Task ArmAsync()
    {
        if (closing) return;
        arm.IsEnabled = check.IsEnabled = mode.IsEnabled = false;
        try
        {
            if (environment is null)
            {
                Directory.CreateDirectory(directory); var factory = new SqliteAgentDbContextFactory(Path.Combine(directory, "synthetic.db"));
                await using (var db = factory.CreateDbContext()) await db.Database.MigrateAsync();
                repository = new(factory); runs = new(repository, desktop);
                takeover = new(desktop, runs, NullLogger<TakeControlHotkeyService>.Instance);
                environment = new(desktop, takeover, NullLogger<DesktopEnvironmentPauseService>.Instance);
                if (!await environment.StartAsync()) throw new InvalidOperationException("EnvironmentObservationUnavailable");
            }
            if (closing) return;
            if (runId is { } old)
            {
                var previous = runs!.GetRun(old); await runs.CancelAsync(new(Guid.NewGuid(), old, previous.RunVersion));
                desktop.ResumeRun(old); // Deliberate ARM only; never automatic recovery or native input permission.
            }
            selected = (string)mode.SelectedItem;
            before = Enum.GetValues<DesktopEnvironmentChange>().Select(environment.ChangeCount).ToArray();
            var run = await runs!.SubmitAsync(new(Guid.NewGuid(), "Synthetic lifecycle cycle", new("attended-diagnostic")));
            runId = run.Id; lease = await desktop.AcquireAsync(run.Id);
            Write($"PA113 ARMED; mode={selected}; generation={lease.Generation}; perform the selected Windows cycle, then CHECK. No input/capture.");
            check.IsEnabled = true;
        }
        catch (Exception error) { Write($"PA113 BLOCKED; type={error.GetType().Name}; no input/capture."); }
    }
    private async Task CheckAsync()
    {
        if (closing || runId is not { } id || environment is null || takeover is null || repository is null || lease is null) return;
        check.IsEnabled = false;
        try
        {
            await takeover.Completion.WaitAsync(TimeSpan.FromSeconds(10));
            var delta = Enum.GetValues<DesktopEnvironmentChange>().Select((value, index) => environment.ChangeCount(value) - before[index]).ToArray();
            var observed = selected switch { "Lock/unlock" => delta[0] >= 2, "Sleep/wake" => delta[1] > 0 && delta[2] > 0, _ => delta[3] > 0 };
            var stored = (await repository.GetAsync(id))!;
            var durable = takeover.Handoff.Confirmed && stored.Run.Reason == nameof(PauseReason.DesktopEnvironmentChanged) &&
                stored.Events.Any(e => e.EventType == RunEventType.DesktopPauseConfirmed);
            var revoked = !desktop.IsCurrent(lease);
            var paused = stored.Run.Status == RunStatus.Paused && stored.Run.PauseScope == PauseScope.EntireRun && desktop.Snapshot.State == DesktopAccessState.PausedForUser;
            var ok = observed && revoked && paused && durable && environment.IsMonitoring;
            Write($"PA113 ENVIRONMENT {(ok ? "OK" : "FAILED")}; mode={selected}; sessionEvents={delta[0]}; suspendEvents={delta[1]}; resumeEvents={delta[2]}; displayEvents={delta[3]}; ownedLeaseRevoked={revoked}; checkpoint={durable}; noAutoResume={paused}; observerSubscribed={environment.IsMonitoring}; nativeInputCalls=0; captures=0.");
            arm.IsEnabled = mode.IsEnabled = ok; check.IsEnabled = !ok;
        }
        catch (Exception error) { Write($"PA113 BLOCKED; type={error.GetType().Name}; no input/capture."); }
    }
    private void Write(string value) { report.AppendText(value + Environment.NewLine); report.ScrollToEnd(); }
    private async void CloseAsync(object? sender, CancelEventArgs args)
    {
        if (closed) return; args.Cancel = true; if (closing) return; closing = true;
        arm.IsEnabled = check.IsEnabled = mode.IsEnabled = false;
        try
        {
            await Task.WhenAll(arming ?? Task.CompletedTask, checking ?? Task.CompletedTask);
            if (environment is not null) await environment.DisposeAsync();
            if (takeover is not null) await takeover.Completion.WaitAsync(TimeSpan.FromSeconds(10));
            takeover?.Dispose(); runs?.Dispose();
            var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "PersonalAgent.Environment")) + Path.DirectorySeparatorChar;
            if (Path.GetFullPath(directory).StartsWith(root, StringComparison.OrdinalIgnoreCase) && Directory.Exists(directory)) Directory.Delete(directory, true);
        }
        catch (Exception) { /* Preserve a scoped temporary directory on cleanup failure; never perform broad cleanup. */ }
        finally { closed = true; Close(); Application.Current.Shutdown(); }
    }
}
