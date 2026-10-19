using PersonalAgent.App.ViewModels;
using System.Windows;

namespace PersonalAgent.App;

public partial class McpConnectionsWindow : Window
{
    public McpConnectionsWindow(McpConnectionsViewModel model)
    {
        InitializeComponent(); DataContext = model;
        model.ClearSecretRequested += ClearCredential;
        Closed += (_, _) => { model.ClearSecretRequested -= ClearCredential; McpCredential.Clear(); };
    }
    private void CredentialChanged(object sender, RoutedEventArgs e)
    { if (DataContext is McpConnectionsViewModel current) current.SetPendingSecret(McpCredential.Password); }
    private void ClearCredential()
    {
        if (Dispatcher.CheckAccess()) McpCredential.Clear();
        else if (!Dispatcher.HasShutdownStarted) Dispatcher.BeginInvoke(new Action(() => McpCredential.Clear()));
    }
}
