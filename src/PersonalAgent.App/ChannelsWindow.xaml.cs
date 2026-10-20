using System.Windows;
using PersonalAgent.App.ViewModels;

namespace PersonalAgent.App;

public partial class ChannelsWindow : Window
{
    public ChannelsWindow(ChannelsViewModel model)
    {
        InitializeComponent(); DataContext = model; model.ClearSecretRequested += ClearToken;
        Closed += (_, _) => { model.ClearSecretRequested -= ClearToken; model.ClearSecret(); BotToken.Clear(); };
    }
    private void TokenChanged(object sender, RoutedEventArgs e) { if (DataContext is ChannelsViewModel model) model.SetPendingSecret(BotToken.Password); }
    private void ClearToken() { if (Dispatcher.CheckAccess()) BotToken.Clear(); else if (!Dispatcher.HasShutdownStarted) Dispatcher.BeginInvoke(new Action(() => BotToken.Clear())); }
}
