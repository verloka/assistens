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
    private McpConnectionsWindow? mcp;
    private readonly PersonalAgent.App.ViewModels.McpConnectionsViewModel? mcpConnections;
    public MainWindow(PersonalAgent.App.ViewModels.HealthViewModel viewModel, PersonalAgent.App.ViewModels.McpConnectionsViewModel? mcpConnections = null)
    {
        InitializeComponent();
        DataContext = viewModel;
        this.mcpConnections = mcpConnections;
    }

    private void OpenModelsClicked(object sender, RoutedEventArgs e) => OpenModels();
    private void OpenMcpClicked(object sender, RoutedEventArgs e) => OpenMcpConnections();
    public void OpenMcpConnections()
    {
        if (mcpConnections is null) return;
        if (mcp is not null) { mcp.Activate(); return; }
        mcp = new McpConnectionsWindow(mcpConnections) { Owner = this };
        mcp.Closed += (_, _) => mcp = null;
        mcp.Show();
    }

    public void OpenModels()
    {
        if (DataContext is not PersonalAgent.App.ViewModels.HealthViewModel { Dashboard.Profiles: { } profiles }) return;
        if (models is not null) { models.Activate(); return; }
        models = new ModelProfilesWindow(profiles) { Owner = this };
        models.Closed += (_, _) => models = null;
        models.Show();
    }
}
