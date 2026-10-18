using System.ComponentModel;
using System.Windows;
using System.Windows.Threading;
using PersonalAgent.App.ViewModels;

namespace PersonalAgent.App;

// Window lifetime and tray callbacks only. Showing/hiding never changes task/desktop policy.
public sealed class DesktopShell(MainWindow main, ControlWindow controls, ControlPanelViewModel model,
    ITrayIcon tray, Action exit) : IDisposable
{
    private readonly DispatcherTimer refresh = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private bool started, disposed;
    public void Start()
    {
        if (started) return;
        ObjectDisposedException.ThrowIf(disposed, this);
        try { tray.Start(model, Open, OpenControls, exit); }
        catch { tray.Dispose(); throw; }
        started = true;
        main.Closing += OnMainClosing;
        controls.Closing += OnControlsClosing;
        main.OpenControlsButton.Click += OnOpenControls;
        refresh.Tick += OnRefresh; refresh.Start();
    }
    private void OnRefresh(object? sender, EventArgs e) { model.Refresh(); tray.Refresh(model.Status); }
    private void OnOpenControls(object sender, RoutedEventArgs e) => OpenControls();
    public void Open()
    {
        if (disposed) return;
        main.Show(); main.WindowState = WindowState.Normal; main.Activate(); // Explicit user Open only.
    }
    public void OpenControls()
    {
        if (disposed) return;
        model.Refresh(); controls.Show(); controls.WindowState = WindowState.Normal;
    }
    private void OnMainClosing(object? sender, CancelEventArgs e)
    {
        e.Cancel = true;
        if (main.CloseToTrayCheckBox.IsChecked == true) main.Hide();
        else exit();
    }
    private void OnControlsClosing(object? sender, CancelEventArgs e) { e.Cancel = true; controls.Hide(); }
    public void Dispose()
    {
        if (disposed) return;
        disposed = true; refresh.Stop(); refresh.Tick -= OnRefresh;
        main.Closing -= OnMainClosing; controls.Closing -= OnControlsClosing;
        main.OpenControlsButton.Click -= OnOpenControls;
        tray.Dispose(); controls.Close();
    }
}
