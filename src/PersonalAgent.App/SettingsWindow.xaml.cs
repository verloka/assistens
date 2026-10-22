using Microsoft.Win32;
using PersonalAgent.App.ViewModels;
using PersonalAgent.Persistence;
using System.IO;
using System.Text;
using System.Windows;

namespace PersonalAgent.App;

public partial class SettingsWindow : Window
{
    private readonly SettingsViewModel model;
    public SettingsWindow(SettingsViewModel model)
    {
        InitializeComponent(); this.model = model; DataContext = model;
        Loaded += (_, _) => model.ReloadStartup();
    }
    private async void ExportClicked(object sender, RoutedEventArgs args)
    {
        var dialog = new SaveFileDialog { Filter = "Settings JSON|*.json", FileName = "personal-agent-settings.json", OverwritePrompt = true };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            var json = await model.ExportAsync();
            if (json is not null) await File.WriteAllTextAsync(dialog.FileName, json, new UTF8Encoding(false));
        }
        catch (Exception) { model.FileFailure(); }
    }
    private async void LoadClicked(object sender, RoutedEventArgs args)
    {
        var dialog = new OpenFileDialog { Filter = "Settings JSON|*.json", CheckFileExists = true };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            await using var stream = new FileStream(dialog.FileName, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (stream.Length > SettingsTransfer.MaximumBytes) throw new InvalidDataException();
            using var reader = new StreamReader(stream, Encoding.UTF8, true);
            model.Load(await reader.ReadToEndAsync());
        }
        catch (Exception) { model.FileFailure(); }
    }
    private async void ApplyClicked(object sender, RoutedEventArgs args) => await model.ApplyAsync();
    private async void BackupClicked(object sender, RoutedEventArgs args) => await model.BackupAsync();
    private void ReloadStartupClicked(object sender, RoutedEventArgs args) => model.ReloadStartup();
    private void SaveStartupClicked(object sender, RoutedEventArgs args) => model.SaveStartup();
}
