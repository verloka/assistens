using System.ComponentModel;
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
    private bool _shutdownComplete;

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

            MainWindow = host.Services.GetRequiredService<MainWindow>();
            MainWindow.Closing += OnMainWindowClosing;
            MainWindow.Show();
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

    private void OnMainWindowClosing(object? sender, CancelEventArgs e)
    {
        if (_shutdownComplete)
            return;

        e.Cancel = true;
        BeginShutdown();
    }

    private async void BeginShutdown(int exitCode = 0)
    {
        if (_shutdownStarted)
            return;

        _shutdownStarted = true;
        _stoppingRegistration.Dispose();
        try
        {
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
            _shutdownComplete = true;
            Shutdown(exitCode);
        }
    }

    private void ReportFailure(Exception exception, string message)
    {
        _logger?.LogError(exception, "{Message}", message);
        Trace.TraceError("{0} {1}", message, exception);
    }
}
