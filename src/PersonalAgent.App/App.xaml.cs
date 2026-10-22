using System.Diagnostics;
using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace PersonalAgent.App;

public partial class App : Application
{
    private DesktopHost? _desktopHost;
    private ILogger<App>? _logger;
    private CancellationTokenRegistration _stoppingRegistration;
    private bool _shutdownStarted;
    private TakeControlHotkeyService? _takeControl;
    private UserActivityPauseService? _activity;
    private DesktopEnvironmentPauseService? _environment;
    private DesktopShell? _shell;
    private DesktopNotificationSink? _notificationSink;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        if (e.Args.SequenceEqual(["--package-smoke"]))
        {
            try { await PackageSmoke.RunAsync(); Shutdown(0); }
            catch (Exception) { Console.Error.WriteLine("PACKAGE SMOKE FAILED"); Shutdown(1); }
            return;
        }
        try
        {
            var host = AppHost.CreateBuilder(e.Args, AppHost.DefaultHistoryPath).Build();
            _desktopHost = new DesktopHost(host);
            _logger = host.Services.GetRequiredService<ILogger<App>>();
            _stoppingRegistration = host.Services.GetRequiredService<IHostApplicationLifetime>()
                .ApplicationStopping.Register(() => Dispatcher.BeginInvoke(new Action(() =>
                {
                    if (!_shutdownStarted)
                        BeginShutdown();
                })));

            await _desktopHost.StartAsync();
            if (_shutdownStarted)
                return;

            _takeControl = host.Services.GetRequiredService<TakeControlHotkeyService>();
            if (!_takeControl.Start())
                MessageBox.Show("Ctrl+Shift+F10 is unavailable. Desktop automation remains blocked; see diagnostic output.",
                    "Take Control shortcut unavailable", MessageBoxButton.OK, MessageBoxImage.Warning);
            _activity = host.Services.GetRequiredService<UserActivityPauseService>();
            if (!await _activity.StartAsync(host.Services.GetRequiredService<IHostApplicationLifetime>().ApplicationStopping))
                _logger.LogWarning("Input observation is unavailable; desktop automation remains blocked.");
            if (_shutdownStarted) return;
            _environment = host.Services.GetRequiredService<DesktopEnvironmentPauseService>();
            if (!await _environment.StartAsync()) _logger.LogWarning("Desktop environment observation is unavailable; GUI remains blocked.");
            if (_shutdownStarted) return;
            MainWindow = host.Services.GetRequiredService<MainWindow>();
            var tray = new WindowsTrayIcon();
            _shell = new DesktopShell((MainWindow)MainWindow, host.Services.GetRequiredService<ControlWindow>(),
                host.Services.GetRequiredService<ViewModels.ControlPanelViewModel>(), tray, () => BeginShutdown());
            _shell.Start();
            _notificationSink = host.Services.GetRequiredService<DesktopNotificationSink>();
            _notificationSink.Attach(Dispatcher, tray);
            MainWindow.Show();
            ((MainWindow)MainWindow).StartDesktopWarnings();
            if (e.Args.Contains("--models", StringComparer.Ordinal)) ((MainWindow)MainWindow).OpenModels();
            else
            {
                var profiles = host.Services.GetRequiredService<ViewModels.ModelProfilesViewModel>();
                await profiles.Initialization;
                if (!_shutdownStarted && (e.Args.Contains("--setup", StringComparer.Ordinal)
                    || profiles.Choices.Count == 1 && host.Services.GetRequiredService<PersonalAgent.Core.Application.TaskRunCoordinator>().GetRuns().Count == 0))
                    ((MainWindow)MainWindow).OpenSetup();
            }
        }
        catch (OperationCanceledException) when (_shutdownStarted)
        {
            // Closing during startup is a normal shutdown path.
        }
        catch (Exception exception)
        {
            ReportFailure(exception, "Application startup failed.");
            MessageBox.Show("Personal Agent could not start. See diagnostic output for details.",
                "Personal Agent", MessageBoxButton.OK, MessageBoxImage.Error);
            BeginShutdown(1);
        }
    }

    private async void BeginShutdown(int exitCode = 0)
    {
        if (_shutdownStarted)
            return;

        _shutdownStarted = true;
        _stoppingRegistration.Dispose();
        try
        {
            if (_environment is not null) { await _environment.DisposeAsync(); _environment = null; }
            if (_activity is not null) { await _activity.DisposeAsync(); _activity = null; }
            _takeControl?.Dispose(); _takeControl = null;
            _notificationSink?.Detach(); _notificationSink = null;
            _shell?.Dispose(); _shell = null;
            if (_desktopHost is not null)
                await _desktopHost.StopAsync();
        }
        catch (Exception exception)
        {
            ReportFailure(exception, "Application shutdown failed.");
            exitCode = 1;
        }
        finally
        {
            Shutdown(exitCode);
        }
    }

    private void ReportFailure(Exception exception, string message)
    {
        _logger?.LogError(exception, "{Message}", message); // The standard formatter exposes only metadata/type.
        Trace.TraceError("Personal Agent failure ({0}).", exception.GetType().Name);
    }
}
