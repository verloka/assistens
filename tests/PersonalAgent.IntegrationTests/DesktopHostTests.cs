using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using PersonalAgent.App;

namespace PersonalAgent.IntegrationTests;

public sealed class DesktopHostTests
{
    [Fact]
    public async Task HostStartsServicesThenCancelsStopsAndDisposesThemOnce()
    {
        var worker = new CancellableWorker();
        var logs = new RecordingLoggerProvider();
        var host = AppHost.CreateBuilder([])
            .ConfigureLogging(logging => logging.ClearProviders().AddProvider(logs))
            .ConfigureServices(services => services.AddSingleton<IHostedService>(_ => worker))
            .Build();
        var lifetime = host.Services.GetRequiredService<IHostApplicationLifetime>();
        var stopping = lifetime.ApplicationStopping;
        var stopped = lifetime.ApplicationStopped;
        var desktop = new DesktopHost(host);

        try
        {
            var start = desktop.StartAsync();
            Assert.Same(start, desktop.StartAsync());
            await start.WaitAsync(TimeSpan.FromSeconds(5));
            await worker.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(lifetime.ApplicationStarted.IsCancellationRequested);

            var stop = desktop.StopAsync();
            Assert.Same(stop, desktop.StopAsync());
            await stop.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.True(worker.Cancelled);
            Assert.Equal(1, worker.DisposeCount);
            Assert.True(stopping.IsCancellationRequested);
            Assert.True(stopped.IsCancellationRequested);
            Assert.Single(logs.Events, item => item.Id == 1000);
            Assert.Single(logs.Events, item => item.Id == 1001);
            Assert.Throws<InvalidOperationException>(() => { _ = desktop.StartAsync(); });
        }
        finally
        {
            await desktop.StopAsync();
        }
    }

    [Fact]
    public async Task StartupFailurePropagatesAndPartialServicesAreStoppedAndDisposed()
    {
        var first = new ProbeService();
        var failing = new ProbeService(start: _ => throw new InvalidOperationException("Startup failed"));
        var host = CreateHost(first, failing);
        var desktop = new DesktopHost(host);
        try
        {
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() => desktop.StartAsync());
            Assert.Equal("Startup failed", error.Message);
        }
        finally
        {
            await desktop.StopAsync();
        }

        Assert.Equal(1, first.StopCount);
        Assert.Equal(1, first.DisposeCount);
        Assert.Equal(1, failing.DisposeCount);
    }

    [Fact]
    public async Task ClosingDuringStartupCancelsStartupAndCleansUpServices()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var slow = new ProbeService(start: async token =>
        {
            entered.SetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
        });
        var desktop = new DesktopHost(CreateHost(slow));
        var startup = desktop.StartAsync();
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await desktop.StopAsync().WaitAsync(TimeSpan.FromSeconds(5));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => startup);
            Assert.Equal(1, slow.StopCount);
            Assert.Equal(1, slow.DisposeCount);
        }
        finally
        {
            await desktop.StopAsync();
        }
    }

    [Fact]
    public async Task ShutdownFailureIsReportedAndStillDisposesServices()
    {
        var failing = new ProbeService(stop: _ => throw new InvalidOperationException("Shutdown failed"));
        var desktop = new DesktopHost(CreateHost(failing));
        await desktop.StartAsync();
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => desktop.StopAsync());
        Assert.Equal("Shutdown failed", error.Message);
        Assert.Equal(1, failing.DisposeCount);
    }

    private static IHost CreateHost(params IHostedService[] services) => AppHost.CreateBuilder([])
        .ConfigureLogging(logging => logging.ClearProviders())
        .ConfigureServices(collection =>
        {
            foreach (var service in services)
                collection.AddSingleton<IHostedService>(_ => service);
        }).Build();

    private sealed class ProbeService(
        Func<CancellationToken, Task>? start = null,
        Func<CancellationToken, Task>? stop = null) : IHostedService, IDisposable
    {
        public int StopCount { get; private set; }
        public int DisposeCount { get; private set; }

        public Task StartAsync(CancellationToken cancellationToken) => start?.Invoke(cancellationToken) ?? Task.CompletedTask;

        public Task StopAsync(CancellationToken cancellationToken)
        {
            StopCount++;
            return stop?.Invoke(cancellationToken) ?? Task.CompletedTask;
        }

        public void Dispose() => DisposeCount++;
    }

    private sealed class CancellableWorker : BackgroundService
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool Cancelled { get; private set; }
        public int DisposeCount { get; private set; }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            Started.SetResult();
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                Cancelled = true;
            }
        }

        public override void Dispose()
        {
            DisposeCount++;
            base.Dispose();
        }
    }

    private sealed class RecordingLoggerProvider : ILoggerProvider
    {
        public ConcurrentQueue<EventId> Events { get; } = new();
        public ILogger CreateLogger(string categoryName) => new RecordingLogger(Events);
        public void Dispose() { }

        private sealed class RecordingLogger(ConcurrentQueue<EventId> events) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => true;
            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
                Func<TState, Exception?, string> formatter) => events.Enqueue(eventId);
        }
    }
}
