using Microsoft.Win32;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.DependencyInjection;
using PersonalAgent.App;
using PersonalAgent.App.ViewModels;
using PersonalAgent.Core.Security;
using System.IO;
using System.Windows.Controls;

namespace PersonalAgent.IntegrationTests;

[Collection("Desktop UI")]
public sealed class LoginStartupTests
{
    [Fact]
    public Task ActualSettingsXamlNeedsExplicitSaveAndRegistryIsPerUserQuotedAndRemovable() => PrivacyUiTests.OnWpfThread(async () =>
    {
        using var fixture = new StartupFixture();
        using var host = Microsoft.Extensions.Hosting.Host.CreateDefaultBuilder().Build();
        var model = new SettingsViewModel(null, new ApprovalOwner("desktop", "fixture"), host.Services.GetRequiredService<IHostApplicationLifetime>(), startup: fixture.Startup);
        var window = new SettingsWindow(model);
        try
        {
            window.Measure(new(700, 510)); window.Arrange(new(0, 0, 700, 510)); window.UpdateLayout();
            await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            model.ReloadStartup(); Assert.False(model.LoginStartupEnabled); Assert.False(fixture.Startup.ReadEnabled());
            Assert.NotNull(window.FindName("SaveLoginStartup"));
            var check = (CheckBox)window.FindName("LoginStartupCheckBox"); check.SetCurrentValue(CheckBox.IsCheckedProperty, true);
            check.GetBindingExpression(CheckBox.IsCheckedProperty)!.UpdateSource();
            Assert.True(model.LoginStartupEnabled); Assert.False(fixture.Startup.ReadEnabled());
            ((Button)window.FindName("SaveLoginStartup")).RaiseEvent(new System.Windows.RoutedEventArgs(Button.ClickEvent));
            Assert.Contains("enabled", model.StartupMessage);
            using (var key = Registry.CurrentUser.OpenSubKey(fixture.KeyPath))
            {
                Assert.Equal("\"" + fixture.Executable + "\"", key!.GetValue("PersonalAgent.App"));
                Assert.Equal(RegistryValueKind.String, key.GetValueKind("PersonalAgent.App"));
                Assert.Equal("unrelated", key.GetValue("OtherProduct"));
            }
            var reopened = new WindowsLoginStartup(fixture.Executable, fixture.KeyPath); Assert.True(reopened.ReadEnabled());
            model.LoginStartupEnabled = false; model.SaveStartup(); Assert.False(reopened.ReadEnabled());
            Assert.Contains("disabled", model.StartupMessage);
            using var remaining = Registry.CurrentUser.OpenSubKey(fixture.KeyPath); Assert.Equal("unrelated", remaining!.GetValue("OtherProduct"));
            await Task.CompletedTask;
        }
        finally { window.Close(); }
    });

    [Fact]
    public void MissingDevelopmentOrUnquotedForeignExecutablesCannotCreateOrReplaceStartup()
    {
        using var fixture = new StartupFixture();
        Assert.False(new WindowsLoginStartup(Path.Combine(fixture.Directory.FullName, "dotnet.exe"), fixture.KeyPath).CanEnable);
        Assert.Throws<InvalidOperationException>(() => new WindowsLoginStartup(Path.Combine(fixture.Directory.FullName, "missing", "PersonalAgent.App.exe"), fixture.KeyPath).SetEnabled(true));
        using var key = Registry.CurrentUser.OpenSubKey(fixture.KeyPath, writable: true);
        key!.SetValue("PersonalAgent.App", "unquoted-foreign-command --arguments");
        Assert.Throws<InvalidOperationException>(() => fixture.Startup.ReadEnabled());
        Assert.Throws<InvalidOperationException>(() => fixture.Startup.SetEnabled(true));
        Assert.Throws<InvalidOperationException>(() => fixture.Startup.SetEnabled(false));
        Assert.Equal("unquoted-foreign-command --arguments", key.GetValue("PersonalAgent.App"));
    }

    [Fact]
    public void ExplicitSaveUpdatesMovedProductPathButOpeningAndDisablingDoNotCreateKeys()
    {
        using var fixture = new StartupFixture();
        var otherKey = fixture.KeyPath + @"\NotCreated";
        var absent = new WindowsLoginStartup(fixture.Executable, otherKey);
        Assert.False(absent.ReadEnabled()); absent.SetEnabled(false);
        Assert.Null(Registry.CurrentUser.OpenSubKey(otherKey));
        using var key = Registry.CurrentUser.OpenSubKey(fixture.KeyPath, writable: true);
        key!.SetValue("PersonalAgent.App", "\"C:\\Old Folder\\PersonalAgent.App.exe\"");
        Assert.True(fixture.Startup.ReadEnabled()); fixture.Startup.SetEnabled(true);
        Assert.Equal("\"" + fixture.Executable + "\"", key.GetValue("PersonalAgent.App"));
    }

    private sealed class StartupFixture : IDisposable
    {
        public DirectoryInfo Directory { get; } = System.IO.Directory.CreateTempSubdirectory("PersonalAgent.Login Test-");
        public string KeyPath { get; } = @"Software\PersonalAgent\Tests\" + Guid.NewGuid().ToString("N");
        public string Executable { get; }
        public WindowsLoginStartup Startup { get; }
        public StartupFixture()
        {
            Executable = Path.Combine(Directory.FullName, "PersonalAgent.App.exe"); File.WriteAllBytes(Executable, []);
            Startup = new(Executable, KeyPath);
            using var key = Registry.CurrentUser.CreateSubKey(KeyPath); key.SetValue("OtherProduct", "unrelated");
        }
        public void Dispose() { Registry.CurrentUser.DeleteSubKeyTree(KeyPath, throwOnMissingSubKey: false); Directory.Delete(recursive: true); }
    }
}
