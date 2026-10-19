using System.Windows;
using System.Windows.Controls;
using PersonalAgent.App.ViewModels;

namespace PersonalAgent.App;

public partial class ModelProfilesControl : UserControl
{
    public ModelProfilesControl()
    {
        InitializeComponent();
        DataContextChanged += (_, change) =>
        {
            if (change.OldValue is ModelProfilesViewModel old) old.ClearSecretRequested -= ClearPassword;
            ModelApiKey.Clear();
            if (change.NewValue is ModelProfilesViewModel current) current.ClearSecretRequested += ClearPassword;
        };
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
