using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using PersonalAgent.App;
using PersonalAgent.App.ViewModels;
using PersonalAgent.Core.Application;
using PersonalAgent.Core.Computer;
using PersonalAgent.Core.Domain;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace PersonalAgent.IntegrationTests;

[Collection("Desktop UI")]
public sealed class DesktopShellTests
{
    [Fact]
    public Task CloseToTrayKeepsCommandsAvailableAndMiniWindowShowsPausedWithoutOpeningAnyWindow() => OnWpfThread(async () =>
    {
        using var host = Builder().Build();
        var runs = host.Services.GetRequiredService<TaskRunCoordinator>();
        var run = await runs.SubmitAsync(new(Guid.NewGuid(), "Tray test task", new("test")));
        var main = host.Services.GetRequiredService<MainWindow>();
        var controls = host.Services.GetRequiredService<ControlWindow>();
        var model = host.Services.GetRequiredService<ControlPanelViewModel>();
        var tray = new TrayProbe(); var exits = 0; var closed = false;
        main.Closed += (_, _) => closed = true;
        using var shell = new DesktopShell(main, controls, model, tray, () => exits++);
        try
        {
            shell.Start(); shell.Start();
            ((CheckBox)main.FindName("CloseToTrayCheckBox")).IsChecked = true;
            main.Close();
            Assert.False(closed); Assert.Equal(0, exits); Assert.Equal(1, tray.Starts);
            Assert.False(main.IsVisible); Assert.False(controls.IsVisible);
            Assert.NotNull(tray.Open); Assert.NotNull(tray.Controls); Assert.NotNull(tray.Exit);
            await Bind(controls);
            Assert.Same(model.TakeControlCommand, ((Button)controls.FindName("TakeControlButton")).Command);
            Assert.Same(model.ResumeCommand, ((Button)controls.FindName("ControlResumeButton")).Command);
            Assert.Same(model.CancelCommand, ((Button)controls.FindName("ControlCancelButton")).Command);
            tray.Model!.TakeControlCommand.Execute(null);
            await model.TakeControlCommand.ExecutionTask!;
            await Bind(controls);
            Assert.Equal(RunStatus.Paused, runs.GetRun(run.Id).Status);
            Assert.Equal("Paused", ((TextBlock)controls.FindName("ControlStatus")).Text);
            Assert.Equal("Tray test task", model.RunTitle);
            Assert.False(controls.ShowActivated); Assert.False(controls.Topmost);
            Assert.False(controls.IsVisible); Assert.False(controls.IsActive);
            Assert.True(model.ResumeCommand.CanExecute(null));
            Assert.Equal(DesktopAccessState.PausedForUser, host.Services.GetRequiredService<IDesktopAccessCoordinator>().Snapshot.State);
        }
        finally { shell.Dispose(); main.Close(); }
        Assert.True(closed); Assert.Equal(1, tray.Disposals);
    });

    [Fact]
    public Task CloseWithoutOptInRequestsExitAndShutdownDisposesTrayOnlyOnce() => OnWpfThread(async () =>
    {
        using var host = Builder().Build();
        var main = host.Services.GetRequiredService<MainWindow>();
        var controls = host.Services.GetRequiredService<ControlWindow>();
        var tray = new TrayProbe(); var exits = 0;
        var shell = new DesktopShell(main, controls, host.Services.GetRequiredService<ControlPanelViewModel>(), tray, () => exits++);
        shell.Start();
        Assert.NotEqual(true, ((CheckBox)main.FindName("CloseToTrayCheckBox")).IsChecked);
        main.Close(); Assert.Equal(1, exits);
        shell.Dispose(); shell.Dispose(); main.Close();
        Assert.Equal(1, tray.Disposals);
        await Task.CompletedTask;
    });

    [Fact]
    public Task ExplicitResumeInvalidatesOldLeaseAndCancelDoesNotReopenGlobalHumanPause() => OnWpfThread(async () =>
    {
        var desktop = new DesktopAccessCoordinator(_ => true);
        using var host = Builder().ConfigureServices(s => s.AddSingleton<IDesktopAccessCoordinator>(desktop)).Build();
        var runs = host.Services.GetRequiredService<TaskRunCoordinator>();
        var run = await runs.SubmitAsync(new(Guid.NewGuid(), "Resume only explicitly", new("test")));
        var lease = await desktop.AcquireAsync(run.Id);
        var model = host.Services.GetRequiredService<ControlPanelViewModel>();
        model.TakeControlCommand.Execute(null); await model.TakeControlCommand.ExecutionTask!;
        Assert.False(desktop.IsCurrent(lease));
        Assert.True(model.ResumeCommand.CanExecute(null));
        model.ResumeCommand.Execute(null); await model.ResumeCommand.ExecutionTask!;
        var fresh = await desktop.AcquireAsync(run.Id);
        Assert.True(fresh.Generation > lease.Generation); Assert.False(desktop.IsCurrent(lease));
        model.TakeControlCommand.Execute(null); await model.TakeControlCommand.ExecutionTask!;
        model.CancelCommand.Execute(null); await model.CancelCommand.ExecutionTask!;
        Assert.Equal(RunStatus.Cancelled, runs.GetRun(run.Id).Status);
        Assert.False(model.ResumeCommand.CanExecute(null));
        Assert.Equal("Paused", model.Status);
        var error = await Assert.ThrowsAsync<DesktopAccessException>(() => desktop.AcquireAsync(Guid.NewGuid()));
        Assert.Equal("DesktopPaused", error.Code);
    });

    [Fact]
    public Task AtomicWorkStaysPausingAndResumeUnavailableUntilDrainWithoutPhysicalReleaseClaim() => OnWpfThread(async () =>
    {
        var desktop = new DesktopAccessCoordinator(_ => true);
        using var host = Builder().ConfigureServices(s => s.AddSingleton<IDesktopAccessCoordinator>(desktop)).Build();
        var runs = host.Services.GetRequiredService<TaskRunCoordinator>();
        var run = await runs.SubmitAsync(new(Guid.NewGuid(), "Atomic test", new("test")));
        var lease = await desktop.AcquireAsync(run.Id);
        using var atomic = desktop.EnterAtomic(lease);
        var model = host.Services.GetRequiredService<ControlPanelViewModel>();
        model.TakeControlCommand.Execute(null);
        Assert.False(desktop.IsCurrent(lease)); // Gate closes synchronously, before awaiting UI command.
        Assert.False(model.TakeControlCommand.ExecutionTask!.IsCompleted);
        Assert.Equal("Pausing", model.Status); Assert.False(model.ResumeCommand.CanExecute(null));
        Assert.DoesNotContain("control is yours", model.Message, StringComparison.OrdinalIgnoreCase);
        atomic.Dispose(); await model.TakeControlCommand.ExecutionTask!; model.Refresh();
        Assert.Equal("Paused", model.Status); Assert.True(model.ResumeCommand.CanExecute(null));
    });

    [Fact]
    public Task FailedTrayStartupDisposesNativeResourceAndDoesNotInstallCloseInterception() => OnWpfThread(async () =>
    {
        using var host = Builder().Build();
        var main = host.Services.GetRequiredService<MainWindow>();
        var controls = host.Services.GetRequiredService<ControlWindow>();
        var tray = new TrayProbe { FailStart = true }; var closed = false;
        main.Closed += (_, _) => closed = true;
        using var shell = new DesktopShell(main, controls, host.Services.GetRequiredService<ControlPanelViewModel>(), tray, () => { });
        Assert.Throws<InvalidOperationException>(shell.Start);
        main.Close(); Assert.True(closed); Assert.Equal(1, tray.Disposals);
        await Task.CompletedTask;
    });

    private static IHostBuilder Builder() => AppHost.CreateBuilder([]).ConfigureLogging(log => log.ClearProviders());
    private sealed class TrayProbe : ITrayIcon
    {
        public int Starts, Disposals;
        public bool FailStart;
        private bool disposed;
        public ControlPanelViewModel? Model;
        public Action? Open, Controls, Exit;
        public void Start(ControlPanelViewModel model, Action open, Action controls, Action exit)
        {
            Starts++; if (FailStart) throw new InvalidOperationException("synthetic tray failure");
            Model = model; Open = open; Controls = controls; Exit = exit;
        }
        public void Refresh(string status) { }
        public void Dispose() { if (!disposed) { disposed = true; Disposals++; } }
    }
    private static async Task Bind(Window window)
    {
        window.Measure(new Size(440, 300)); window.Arrange(new Rect(0, 0, 440, 300)); window.UpdateLayout();
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
    }
    private static Task OnWpfThread(Func<Task> test)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            dispatcher.BeginInvoke(new Action(async () =>
            {
                try { await test(); completion.TrySetResult(); }
                catch (Exception error) { completion.TrySetException(error); }
                finally { dispatcher.BeginInvokeShutdown(DispatcherPriority.Background); }
            }));
            Dispatcher.Run();
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        return completion.Task.WaitAsync(TimeSpan.FromSeconds(20));
    }
}
