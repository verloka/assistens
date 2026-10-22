using PersonalAgent.WindowsSmoke;
using PersonalAgent.Core.Agents;
using PersonalAgent.Core.Computer;
using System.Reflection;
using System.Windows.Controls;
using System.Windows.Threading;

namespace PersonalAgent.IntegrationTests;

[Collection("Desktop UI")]
public sealed class RegressionStandTests
{
    [Fact]
    public Task RuntimeBoundaryFailureIsReportedAsBlockedWithStageAndFixedCode() => OnWpfThread(async () =>
    {
        var window = new RegressionWindow(() => { });
        var desktop = new DesktopAccessCoordinator(); // Denied before any native operation.
        typeof(RegressionWindow).GetField("desktop", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(window, desktop);
        var method = typeof(RegressionWindow).GetMethod("ExecuteAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var request = new AgentRequest { RunId = Guid.NewGuid(), ConversationId = Guid.NewGuid(), ModelProfileId = "fake", Instructions = "Synthetic diagnostic" };
        var result = await (Task<AgentRunResult>)method.Invoke(window, [request, new Progress<AgentEvent>(), CancellationToken.None])!;
        Assert.Equal(AgentResultStatus.Blocked, result.Status); Assert.Equal("DesktopAccessDenied", result.ErrorCode);
        var report = (TextBox)typeof(RegressionWindow).GetField("report", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
        Assert.Contains("PA115 BLOCKED; stage=tray-initial-acquire", report.Text);
        Assert.Contains("code=DesktopAccessDenied", report.Text);
        window.Close(); await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
    });

    [Fact]
    public Task UnarmedConstructionAndCloseDoNotCreateRuntimeTrayOrServer() => OnWpfThread(async () =>
    {
        var exits = 0;
        var window = new RegressionWindow(() => exits++);
        Assert.False(window.IsVisible); Assert.False(window.IsArmed); Assert.False(window.HasResources);
        window.Close();
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        Assert.True(window.IsStopping); Assert.False(window.HasResources); Assert.Equal(1, exits);
    });

    [Theory]
    [InlineData("starting")]
    [InlineData("checking")]
    [InlineData("tickingTask")]
    public Task CloseCancelsAndWaitsForPendingWorkBeforeShutdown(string pendingField) => OnWpfThread(async () =>
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var window = new RegressionWindow(() => finished.TrySetResult());
        typeof(RegressionWindow).GetField(pendingField, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(window, release.Task);
        window.Close();
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        Assert.True(window.IsStopping); Assert.False(finished.Task.IsCompleted); Assert.False(window.HasResources);
        release.TrySetResult();
        await finished.Task.WaitAsync(TimeSpan.FromSeconds(5));
    });

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
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); return completion.Task;
    }
}
