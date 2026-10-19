using PersonalAgent.App.ViewModels;
using System.Windows;
using System.Windows.Threading;

namespace PersonalAgent.App;

public partial class ApprovalsWindow : Window
{
    private readonly DispatcherTimer refresh = new() { Interval = TimeSpan.FromSeconds(1) };
    private bool closed;
    public ApprovalsWindow(ApprovalsViewModel model)
    {
        InitializeComponent(); DataContext = model;
        refresh.Tick += async (_, _) => await model.RefreshAsync();
        Loaded += async (_, _) => { await model.RefreshAsync(); if (!closed) refresh.Start(); };
        Closed += (_, _) => { closed = true; refresh.Stop(); model.Acknowledged = false; };
    }
}
