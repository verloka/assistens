using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Threading;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace PersonalAgent.App.ViewModels;

public sealed class HealthViewModel : INotifyPropertyChanged, IDisposable
{
    private readonly Dispatcher _dispatcher = Dispatcher.CurrentDispatcher;
    private readonly ILogger<HealthViewModel> _logger;
    private readonly CancellationToken _started;
    private readonly CancellationToken _stopping;
    private readonly CancellationToken _stopped;
    private readonly CancellationTokenRegistration[] _registrations;
    private bool _isBusy;
    private bool _disposed;
    private string _diagnosticStatus = "Ready";

    public HealthViewModel(IHostApplicationLifetime lifetime, IEnumerable<IHostedService> services,
        ILogger<HealthViewModel> logger, DashboardViewModel dashboard)
    {
        Dashboard = dashboard;
        _logger = logger;
        _started = lifetime.ApplicationStarted;
        _stopping = lifetime.ApplicationStopping;
        _stopped = lifetime.ApplicationStopped;
        Components = services.Select(service => service.GetType().Name).ToArray();
        RunDiagnosticCommand = new AsyncCommand(RunDiagnosticAsync, () => !IsBusy && HostStatus == "Running" && !_disposed);
        _registrations = [_started.Register(RefreshHost), _stopping.Register(RefreshHost), _stopped.Register(RefreshHost)];
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    public IReadOnlyList<string> Components { get; }
    public DashboardViewModel Dashboard { get; }
    public AsyncCommand RunDiagnosticCommand { get; }
    public string HostStatus => _stopped.IsCancellationRequested ? "Stopped"
        : _stopping.IsCancellationRequested ? "Stopping"
        : _started.IsCancellationRequested ? "Running" : "Starting";

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            _isBusy = value;
            OnPropertyChanged();
            RunDiagnosticCommand.Refresh();
        }
    }

    public string DiagnosticStatus
    {
        get => _diagnosticStatus;
        private set
        {
            _diagnosticStatus = value;
            OnPropertyChanged();
        }
    }

    private async Task RunDiagnosticAsync()
    {
        IsBusy = true;
        DiagnosticStatus = "Running a 5-second test operation...";
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(5), _stopping);
            DiagnosticStatus = "Test operation completed";
        }
        catch (OperationCanceledException) when (_stopping.IsCancellationRequested)
        {
            DiagnosticStatus = "Test operation cancelled: host is stopping";
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Health test operation failed.");
            DiagnosticStatus = "Test operation failed. See diagnostic output.";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void RefreshHost()
    {
        if (_disposed || _dispatcher.HasShutdownStarted)
            return;
        _dispatcher.BeginInvoke(new Action(() =>
        {
            if (_disposed)
                return;
            OnPropertyChanged(nameof(HostStatus));
            RunDiagnosticCommand.Refresh();
        }), DispatcherPriority.DataBind);
    }

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    public void Dispose()
    {
        _disposed = true;
        foreach (var registration in _registrations)
            registration.Dispose();
    }
}
