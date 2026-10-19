using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using PersonalAgent.App;
using PersonalAgent.App.ViewModels;
using PersonalAgent.Core.Persistence;
using PersonalAgent.Core.Security;
using System.Security.Principal;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace PersonalAgent.IntegrationTests;

[Collection("Desktop UI")]
public sealed class ApprovalsUiTests
{
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public Task RealXamlShowsExactActionAndExplicitOwnerDecisionContinuesSameMafRunWithoutBlockingUi(bool approve) => OnWpfThread(async () =>
    {
        await using var fixture = await HumanApprovalTests.Fixture.Start(true); var pending = await fixture.Pending();
        using var host = Host(fixture);
        var main = host.Services.GetRequiredService<MainWindow>(); var model = host.Services.GetRequiredService<ApprovalsViewModel>();
        await model.Initialization; var window = new ApprovalsWindow(model);
        try
        {
            await Bind(main); await Bind(window);
            Assert.NotNull(main.FindName("ApprovalsButton")); Assert.False(window.ShowActivated); Assert.False(window.Topmost); Assert.True(main.IsEnabled);
            Assert.Null(model.Selected); Assert.False(model.ApproveCommand.CanExecute(null));
            ((ListBox)window.FindName("PendingApprovals")).SelectedIndex = 0; await Pump();
            Assert.Contains("synthetic", ((TextBlock)window.FindName("ApprovalDescription")).Text);
            Assert.Equal("synthetic-target", ((TextBlock)window.FindName("ApprovalDestination")).Text);
            Assert.Equal("Write", ((TextBlock)window.FindName("ApprovalRisk")).Text);
            Assert.Contains(pending.Action.ToolId, ((TextBlock)window.FindName("ApprovalTool")).Text);
            Assert.Contains("synthetic-target", ((TextBox)window.FindName("ApprovalArguments")).Text);
            Assert.True(((TextBox)window.FindName("ApprovalArguments")).IsReadOnly);
            Assert.NotEmpty(((TextBlock)window.FindName("ApprovalExpiry")).Text);
            Assert.False(model.ApproveCommand.CanExecute(null)); Assert.True(model.DenyCommand.CanExecute(null)); Assert.Equal(0, fixture.Calls);
            var responsive = false; await main.Dispatcher.InvokeAsync(() => responsive = true); Assert.True(responsive);
            if (approve)
            {
                ((CheckBox)window.FindName("ApprovalAcknowledged")).IsChecked = true; await Pump();
                await model.RefreshAsync(); Assert.True(model.Acknowledged); // Polling unchanged data cannot silently select another action.
            }
            var button = (Button)window.FindName(approve ? "ApproveActionButton" : "DenyApprovalButton");
            Assert.False(button.IsDefault); Assert.True(button.Command!.CanExecute(null)); button.Command.Execute(null);
            await (approve ? model.ApproveCommand : model.DenyCommand).ExecutionTask!.WaitAsync(TimeSpan.FromSeconds(10));
            await fixture.UntilFinished(); await Pump();
            Assert.Equal(approve ? 1 : 0, fixture.Calls); Assert.Null(model.Selected); Assert.False(model.Acknowledged);
            Assert.Equal(approve ? ApprovalStatus.Approved : ApprovalStatus.Rejected, (await fixture.Approvals.GetAsync(pending.Id))!.Status);
            Assert.Equal(approve ? 2 : 1, fixture.Client!.CallCount);
        }
        finally { window.Close(); main.Close(); }
    });

    [Fact]
    public Task DifferentOwnerStaleDecisionAndClosedWindowCannotApproveAnotherAction() => OnWpfThread(async () =>
    {
        await using var fixture = await HumanApprovalTests.Fixture.Start(true); var pending = await fixture.Pending();
        using (var foreignHost = Host(fixture, HumanApprovalTests.Fixture.Owner with { UserId = "other-owner" }))
        {
            var foreign = foreignHost.Services.GetRequiredService<ApprovalsViewModel>(); await foreign.Initialization;
            Assert.Empty(foreign.Pending); foreign.Selected = pending; foreign.Acknowledged = true;
            Assert.False(foreign.ApproveCommand.CanExecute(null)); Assert.False(foreign.DenyCommand.CanExecute(null));
        }
        using var host = Host(fixture); var model = host.Services.GetRequiredService<ApprovalsViewModel>(); await model.Initialization;
        var window = new ApprovalsWindow(model); await Bind(window);
        model.Selected = model.Pending.Single(); model.Acknowledged = true; window.Close();
        Assert.False(model.Acknowledged); Assert.Equal(ApprovalStatus.Pending, (await fixture.Approvals.GetAsync(pending.Id))!.Status); Assert.Equal(0, fixture.Calls);
        model.Acknowledged = true;
        await fixture.Approvals.DecideAsync(new(pending.Id, pending.Version, pending.ActionHash, HumanApprovalTests.Fixture.Owner, false));
        model.ApproveCommand.Execute(null); await model.ApproveCommand.ExecutionTask!;
        Assert.Contains("expired or changed", model.Message); Assert.Null(model.Selected); Assert.False(model.Acknowledged);
        await fixture.UntilFinished(); Assert.Equal(0, fixture.Calls); await model.RefreshAsync(); Assert.Empty(model.Pending);
    });

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public Task RefreshRemovesInvalidatedRunAndExpirationNeverTransfersReviewAcknowledgement(bool expire) => OnWpfThread(async () =>
    {
        await using var fixture = await HumanApprovalTests.Fixture.Start(true); var pending = await fixture.Pending();
        using var host = Host(fixture); var model = host.Services.GetRequiredService<ApprovalsViewModel>(); await model.Initialization;
        model.Selected = model.Pending.Single(); model.Acknowledged = true;
        if (expire) { fixture.Clock.Now += TimeSpan.FromMinutes(2); Assert.False(model.ApproveCommand.CanExecute(null)); }
        else await fixture.Runs.CancelAsync(new(Guid.NewGuid(), fixture.RunId, fixture.Runs.GetRun(fixture.RunId).RunVersion));
        await model.RefreshAsync();
        Assert.Empty(model.Pending); Assert.Null(model.Selected); Assert.False(model.Acknowledged); await fixture.UntilFinished(); Assert.Equal(0, fixture.Calls);
    });

    [Fact]
    public Task SelectingAnotherPendingActionClearsReviewAndNeverDecidesEitherRequest() => OnWpfThread(async () =>
    {
        await using var fixture = await HumanApprovalTests.Fixture.Start(true); var first = await fixture.Pending();
        var second = await fixture.Approvals.CreateAsync(first.Action with { ToolCallId = "different-call", Destination = "another-target", Description = "Another synthetic action" },
            HumanApprovalTests.Fixture.Owner, TimeSpan.FromMinutes(1));
        using var host = Host(fixture); var model = host.Services.GetRequiredService<ApprovalsViewModel>(); await model.Initialization;
        var window = new ApprovalsWindow(model);
        try
        {
            await Bind(window); var list = (ListBox)window.FindName("PendingApprovals");
            list.SelectedItem = model.Pending.Single(value => value.Id == first.Id); model.Acknowledged = true; Assert.True(model.ApproveCommand.CanExecute(null));
            list.SelectedItem = model.Pending.Single(value => value.Id == second.Id); await Pump();
            Assert.False(model.Acknowledged); Assert.False(model.ApproveCommand.CanExecute(null));
            Assert.Equal("another-target", ((TextBlock)window.FindName("ApprovalDestination")).Text);
            Assert.Equal(ApprovalStatus.Pending, (await fixture.Approvals.GetAsync(first.Id))!.Status);
            Assert.Equal(ApprovalStatus.Pending, (await fixture.Approvals.GetAsync(second.Id))!.Status); Assert.Equal(0, fixture.Calls);
        }
        finally { window.Close(); }
    });

    [Fact]
    public Task ProductionOwnerComesFromCurrentWindowsSidAndNoStorageCannotApprove() => OnWpfThread(async () =>
    {
        using var host = AppHost.CreateBuilder([]).Build(); using var identity = WindowsIdentity.GetCurrent();
        Assert.Equal(new ApprovalOwner("desktop", identity.User!.Value), host.Services.GetRequiredService<ApprovalOwner>());
        var model = host.Services.GetRequiredService<ApprovalsViewModel>(); await model.Initialization;
        Assert.Empty(model.Pending); Assert.Contains("unavailable", model.Message);
        Assert.False(model.ApproveCommand.CanExecute(null)); Assert.False(model.DenyCommand.CanExecute(null));
    });

    private static IHost Host(HumanApprovalTests.Fixture fixture, ApprovalOwner? owner = null) => AppHost.CreateBuilder([]).ConfigureServices(services =>
    {
        services.AddSingleton(owner ?? HumanApprovalTests.Fixture.Owner); services.AddSingleton<IApprovalRepository>(fixture.Approvals);
        services.AddSingleton<TimeProvider>(fixture.Clock);
    }).Build(); // No second runner/history recovery is started over the fixture's live run.
    private static async Task Bind(Window window) { window.Measure(new Size(800, 760)); window.Arrange(new Rect(0, 0, 800, 760)); window.UpdateLayout(); await Pump(); }
    private static async Task Pump() => await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
    private static Task OnWpfThread(Func<Task> test)
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher; SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            dispatcher.BeginInvoke(new Action(async () =>
            {
                try { await test(); done.TrySetResult(); } catch (Exception error) { done.TrySetException(error); }
                finally { dispatcher.BeginInvokeShutdown(DispatcherPriority.Background); }
            })); Dispatcher.Run();
        }); thread.SetApartmentState(ApartmentState.STA); thread.Start(); return done.Task.WaitAsync(TimeSpan.FromSeconds(60));
    }
}
