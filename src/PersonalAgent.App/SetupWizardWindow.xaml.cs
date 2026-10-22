using System.Windows;
using PersonalAgent.App.ViewModels;

namespace PersonalAgent.App;

public partial class SetupWizardWindow : Window
{
    public SetupWizardWindow(SetupWizardViewModel model)
    {
        InitializeComponent(); DataContext = model;
        Loaded += async (_, _) => { await model.Profiles.Initialization; model.Refresh(); };
    }
    private void OpenModelsClicked(object sender, RoutedEventArgs args) => (Owner as MainWindow)?.OpenModels();
    private void OpenMcpClicked(object sender, RoutedEventArgs args) => (Owner as MainWindow)?.OpenMcpConnections();
    private void OpenChannelsClicked(object sender, RoutedEventArgs args) => (Owner as MainWindow)?.OpenChannels();
}
