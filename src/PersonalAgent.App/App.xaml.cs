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
    private DesktopShell? _shell;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
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
            MainWindow = host.Services.GetRequiredService<MainWindow>();
            _shell = new DesktopShell((MainWindow)MainWindow, host.Services.GetRequiredService<ControlWindow>(),
                host.Services.GetRequiredService<ViewModels.ControlPanelViewModel>(), new WindowsTrayIcon(), () => BeginShutdown());
            _shell.Start();
            MainWindow.Show();
            if (e.Args.Contains("--models", StringComparer.Ordinal)) ((MainWindow)MainWindow).OpenModels();
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
            _takeControl?.Dispose(); _takeControl = null;
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
        _logger?.LogError(exception, "{Message}", message);
        Trace.TraceError("{0} {1}", message, exception);
    }
}
