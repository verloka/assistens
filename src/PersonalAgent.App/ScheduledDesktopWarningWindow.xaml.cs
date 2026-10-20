using PersonalAgent.App.ViewModels;
using System.Windows;

namespace PersonalAgent.App;

public partial class ScheduledDesktopWarningWindow : Window
{
    public ScheduledDesktopWarningWindow(ScheduledDesktopWarningViewModel model)
    {
        InitializeComponent(); DataContext = model;
        ContentRendered += (_, _) => model.Presented();
        Closed += (_, _) => model.DeferOnClose();
    }
}
