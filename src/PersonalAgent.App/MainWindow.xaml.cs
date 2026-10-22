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
    private ApprovalsWindow? approvals;
    private SchedulesWindow? schedules;
    private ChannelsWindow? channels;
    private PrivacyWindow? privacy;
    private RunDiagnosticsWindow? runDiagnostics;
    private SetupWizardWindow? setup;
    private SettingsWindow? settings;
    private readonly PersonalAgent.App.ViewModels.SettingsViewModel? settingsModel;
    private readonly PersonalAgent.App.ViewModels.SetupWizardViewModel? setupModel;
    private readonly PersonalAgent.App.ViewModels.RunDiagnosticsViewModel? runDiagnosticsModel;
    private readonly PersonalAgent.App.ViewModels.PrivacyViewModel? privacyModel;
    private readonly PersonalAgent.App.ViewModels.ChannelsViewModel? channelModel;
    private readonly PersonalAgent.App.ViewModels.SchedulesViewModel? scheduleModel;
    private readonly ScheduledDesktopWarnings? desktopWarnings;
    private readonly PersonalAgent.App.ViewModels.McpConnectionsViewModel? mcpConnections;
    private readonly PersonalAgent.App.ViewModels.ApprovalsViewModel? approvalModel;
    public MainWindow(PersonalAgent.App.ViewModels.HealthViewModel viewModel, PersonalAgent.App.ViewModels.McpConnectionsViewModel? mcpConnections = null,
        PersonalAgent.App.ViewModels.ApprovalsViewModel? approvalModel = null, PersonalAgent.App.ViewModels.SchedulesViewModel? scheduleModel = null,
        PersonalAgent.Core.Application.ScheduledDesktopGate? scheduledDesktopGate = null, PersonalAgent.App.ViewModels.ChannelsViewModel? channelModel = null,
        PersonalAgent.App.ViewModels.PrivacyViewModel? privacyModel = null,
        PersonalAgent.App.ViewModels.RunDiagnosticsViewModel? runDiagnosticsModel = null,
        PersonalAgent.App.ViewModels.SetupWizardViewModel? setupModel = null,
        PersonalAgent.App.ViewModels.SettingsViewModel? settingsModel = null)
    {
        InitializeComponent();
        DataContext = viewModel;
        this.mcpConnections = mcpConnections;
        this.approvalModel = approvalModel;
        this.scheduleModel = scheduleModel;
        this.channelModel = channelModel;
        this.privacyModel = privacyModel;
        this.runDiagnosticsModel = runDiagnosticsModel;
        this.setupModel = setupModel;
        this.settingsModel = settingsModel;
        if (scheduledDesktopGate is not null) desktopWarnings = new(this, scheduledDesktopGate);
        Closed += (_, _) => desktopWarnings?.Dispose();
    }

    private void OpenSetupClicked(object sender, RoutedEventArgs e) => OpenSetup();
    private void OpenSettingsClicked(object sender, RoutedEventArgs e)
    {
        if (settingsModel is null) return;
        if (settings is not null) { settings.Activate(); return; }
        settings = new SettingsWindow(settingsModel) { Owner = this };
        settings.Closed += (_, _) => settings = null;
        settings.Show();
    }
    public void OpenSetup()
    {
        if (setupModel is null) return;
        if (setup is not null) { setup.Activate(); return; }
        setup = new SetupWizardWindow(setupModel) { Owner = this };
        setup.Closed += (_, _) => setup = null;
        setup.Show();
    }
    private void OpenRunDiagnosticsClicked(object sender, RoutedEventArgs e)
    {
        if (runDiagnosticsModel is null) return;
        if (runDiagnostics is not null) { runDiagnostics.Activate(); return; }
        runDiagnostics = new RunDiagnosticsWindow(runDiagnosticsModel) { Owner = this };
        runDiagnostics.Closed += (_, _) => runDiagnostics = null;
        runDiagnostics.Show();
    }
    private void OpenPrivacyClicked(object sender, RoutedEventArgs e)
    {
        if (privacyModel is null) return;
        if (privacy is not null) { privacy.Activate(); return; }
        privacy = new PrivacyWindow(privacyModel) { Owner = this };
        privacy.Closed += (_, _) => privacy = null;
        privacy.Show();
    }
    private void OpenModelsClicked(object sender, RoutedEventArgs e) => OpenModels();
    private void OpenChannelsClicked(object sender, RoutedEventArgs e) => OpenChannels();
    public void OpenChannels()
    {
        if (channelModel is null) return;
        if (channels is not null) { channels.Activate(); return; }
        channels = new ChannelsWindow(channelModel) { Owner = this };
        channels.Closed += (_, _) => channels = null;
        channels.Show();
    }
    private void OpenDesktopRequestsClicked(object sender, RoutedEventArgs e) => desktopWarnings?.Open();
    public void StartDesktopWarnings() => desktopWarnings?.Start();
    private void OpenSchedulesClicked(object sender, RoutedEventArgs e) => OpenSchedules();
    public void OpenSchedules()
    {
        if (scheduleModel is null) return;
        if (schedules is not null) { schedules.Activate(); return; }
        schedules = new SchedulesWindow(scheduleModel) { Owner = this };
        schedules.Closed += (_, _) => schedules = null;
        schedules.Show();
    }
    private void OpenMcpClicked(object sender, RoutedEventArgs e) => OpenMcpConnections();
    private void OpenApprovalsClicked(object sender, RoutedEventArgs e) => OpenApprovals();
    public void OpenApprovals()
    {
        if (approvalModel is null) return;
        if (approvals is not null) { approvals.Activate(); return; }
        approvals = new ApprovalsWindow(approvalModel) { Owner = this };
        approvals.Closed += (_, _) => approvals = null;
        approvals.Show();
    }
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
