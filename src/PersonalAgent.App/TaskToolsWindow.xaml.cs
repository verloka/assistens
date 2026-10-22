using PersonalAgent.App.ViewModels;
using System.Windows;

namespace PersonalAgent.App;

public partial class TaskToolsWindow : Window
{
    public TaskToolsWindow(TaskToolsViewModel model, DashboardViewModel dashboard)
    { InitializeComponent(); DataContext = model; Tag = dashboard; }
}
