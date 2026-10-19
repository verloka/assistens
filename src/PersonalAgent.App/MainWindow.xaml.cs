using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Navigation;
using System.Windows.Shapes;

namespace PersonalAgent.App;

/// <summary>
/// Interaction logic for MainWindow.xaml
/// </summary>
public partial class MainWindow : Window
{
    private ModelProfilesWindow? models;
    public MainWindow(PersonalAgent.App.ViewModels.HealthViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }

    private void OpenModelsClicked(object sender, RoutedEventArgs e) => OpenModels();

    public void OpenModels()
    {
        if (DataContext is not PersonalAgent.App.ViewModels.HealthViewModel { Dashboard.Profiles: { } profiles }) return;
        if (models is not null) { models.Activate(); return; }
        models = new ModelProfilesWindow(profiles) { Owner = this };
        models.Closed += (_, _) => models = null;
        models.Show();
    }
}
