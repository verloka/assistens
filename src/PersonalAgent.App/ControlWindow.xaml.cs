using System.Windows;
using PersonalAgent.App.ViewModels;

namespace PersonalAgent.App;

public partial class ControlWindow : Window
{
    public ControlWindow(ControlPanelViewModel model) { InitializeComponent(); DataContext = model; }
}
