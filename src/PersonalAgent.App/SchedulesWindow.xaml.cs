using PersonalAgent.App.ViewModels;
using System.Windows;

namespace PersonalAgent.App;

public partial class SchedulesWindow : Window
{
    public SchedulesWindow(SchedulesViewModel model)
    {
        InitializeComponent(); DataContext = model;
    }
}
