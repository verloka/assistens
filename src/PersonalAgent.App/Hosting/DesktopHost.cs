using Microsoft.Extensions.Hosting;

namespace PersonalAgent.App;

// Called on the WPF dispatcher: closing keeps the dispatcher alive until services stop.
public sealed class DesktopHost(IHost host)
{
    private readonly CancellationTokenSource _startupCancellation = new();
    private Task? _startup;
    private Task? _shutdown;
    private bool _stopping;

    public Task StartAsync()
    {
        if (_stopping)
            throw new InvalidOperationException("The desktop host is stopping.");
        return _startup ??= StartCoreAsync();
    }

    public Task StopAsync()
    {
        if (_shutdown is not null)
            return _shutdown;
        _stopping = true;
        return _shutdown = StopCoreAsync();
    }

    private async Task StartCoreAsync() => await host.StartAsync(_startupCancellation.Token);

    private async Task StopCoreAsync()
    {
        try
        {
            await _startupCancellation.CancelAsync();
            if (_startup is not null)
            {
                try
                {
                    await _startup;
                }
                catch
                {
                    // Startup failure is reported by the caller; still stop partial services.
                }
            }

            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await host.StopAsync(timeout.Token);
        }
        finally
        {
            try
            {
                if (host is IAsyncDisposable asyncHost)
                    await asyncHost.DisposeAsync();
                else
                    host.Dispose();
            }
            finally
            {
                _startupCancellation.Dispose();
            }
        }
    }
}
