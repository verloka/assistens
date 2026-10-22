using System.Windows;
using PersonalAgent.App.ViewModels;

namespace PersonalAgent.App;

public partial class PrivacyWindow : Window
{
    public PrivacyWindow(PrivacyViewModel model)
    {
        InitializeComponent(); DataContext = model;
        Loaded += (_, _) => model.ReloadCommand.Execute(null);
    }
}
