using PersonalAgent.App.ViewModels;
using PersonalAgent.Core.Application;
using System.Windows.Threading;

namespace PersonalAgent.App;

// Only the actual App starts this presenter; headless test hosts never create native windows.
public sealed class ScheduledDesktopWarnings(MainWindow owner, ScheduledDesktopGate gate) : IDisposable
{
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private readonly HashSet<Guid> seen = [];
    private ScheduledDesktopWarningWindow? window;
    private Guid? displayed;
    private bool started, disposed;
    public void Start()
    {
        if (started || disposed) return;
        started = true; timer.Tick += Tick; timer.Start();
    }
    private void Tick(object? sender, EventArgs args)
    {
        var requests = gate.GetWarnings();
        if (displayed is { } id && !requests.Any(value => value.Id == id)) window?.Close();
        if (window?.DataContext is ScheduledDesktopWarningViewModel model) model.Refresh();
        if (window is null && requests.FirstOrDefault(value => !seen.Contains(value.Id)) is { } next) Show(next.Id);
    }
    public void Open()
    {
        if (disposed) return;
        if (window is not null) { window.Activate(); return; } // Only deliberate user Open activates.
        if (gate.GetWarnings().FirstOrDefault() is { } next) Show(next.Id);
    }
    private void Show(Guid id)
    {
        seen.Add(id); displayed = id;
        window = new(new ScheduledDesktopWarningViewModel(gate, id)) { Owner = owner };
        window.Closed += (_, _) => { window = null; displayed = null; };
        window.Show();
    }
    public void Dispose() { if (disposed) return; disposed = true; timer.Stop(); timer.Tick -= Tick; window?.Close(); }
}
