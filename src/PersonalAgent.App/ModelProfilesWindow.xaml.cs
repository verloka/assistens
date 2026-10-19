using System.Windows;
using System.Windows.Controls;
using PersonalAgent.App.ViewModels;

namespace PersonalAgent.App;

public partial class ModelProfilesWindow : Window
{
    public ModelProfilesWindow(ModelProfilesViewModel profiles)
    {
        InitializeComponent();
        DataContext = profiles;
        ((Expander)ProfileEditor.FindName("ModelProfileEditor")).IsExpanded = true;
        ((ScrollViewer)ProfileEditor.FindName("ModelProfileScroll")).MaxHeight = double.PositiveInfinity;
    }
}
