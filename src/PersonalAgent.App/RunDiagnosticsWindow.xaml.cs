using System.Windows;
using System.Windows.Threading;
using PersonalAgent.App.ViewModels;

namespace PersonalAgent.App;

public partial class RunDiagnosticsWindow : Window
{
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromSeconds(1) };
    public RunDiagnosticsWindow(RunDiagnosticsViewModel model)
    {
        InitializeComponent(); DataContext = model;
        timer.Tick += (_, _) => model.Refresh();
        Loaded += (_, _) => { model.Refresh(); timer.Start(); };
        Closed += (_, _) => timer.Stop();
    }
}
