using System.Windows;
using System.Windows.Controls;
using PersonalAgent.App.ViewModels;

namespace PersonalAgent.App;

public partial class ModelProfilesControl : UserControl
{
    private ModelProfilesViewModel? subscribed;
    public ModelProfilesControl()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            Unsubscribe();
            ModelApiKey.Clear();
            Subscribe();
        };
        Loaded += (_, _) => Subscribe();
        Unloaded += (_, _) => { ModelApiKey.Clear(); Unsubscribe(); };
    }
    private void Subscribe()
    {
        if (subscribed is not null || DataContext is not ModelProfilesViewModel current) return;
        subscribed = current; current.ClearSecretRequested += ClearPassword;
    }
    private void Unsubscribe()
    {
        if (subscribed is not null) subscribed.ClearSecretRequested -= ClearPassword;
        subscribed = null;
    }
    private void PasswordChanged(object sender, RoutedEventArgs e)
    {
        if (DataContext is ModelProfilesViewModel model) model.SetPendingSecret(ModelApiKey.Password);
    }
    private void ClearPassword()
    {
        if (Dispatcher.CheckAccess()) ModelApiKey.Clear();
        else if (!Dispatcher.HasShutdownStarted) Dispatcher.BeginInvoke(new Action(() => ModelApiKey.Clear()));
    }
}
