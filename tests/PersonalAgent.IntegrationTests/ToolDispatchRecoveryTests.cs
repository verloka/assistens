using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using PersonalAgent.App;
using PersonalAgent.App.ViewModels;
using PersonalAgent.Core.Agents;
using PersonalAgent.Core.Application;
using PersonalAgent.Core.Domain;
using PersonalAgent.Core.Persistence;
using PersonalAgent.IntegrationTests.Fakes;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace PersonalAgent.IntegrationTests;

[Collection("Desktop UI")]
public sealed class ToolDispatchRecoveryTests
{
    [Fact]
    public Task ActualKilledProcessAfterEffectRecoversUnknownDispatchInAppAndNeverReplaysApprovalOrWrite() => OnWpfThread(async () =>
    {
        using var database = new TemporaryAgentDatabase(); var effect = database.DatabasePath + ".effect";
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var child = Process.Start(new ProcessStartInfo("dotnet")
        {
            ArgumentList = { typeof(RecoveryTestProcess.Program).Assembly.Location, database.DatabasePath, effect, "--durable-dispatch" },
            RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true
        })!;
        var errors = child.StandardError.ReadToEndAsync(); Guid runId;
        try
        {
            string? line;
            do { line = await child.StandardOutput.ReadLineAsync(timeout.Token); } while (line is not null && !line.StartsWith("DISPATCH_READY="));
            Assert.NotNull(line); runId = Guid.Parse(line[15..]);
            Assert.Equal(["effect"], await File.ReadAllLinesAsync(effect, timeout.Token));
            var before = Assert.Single(await new Persistence.SqliteToolCallRepository(database).ListAsync(runId));
            Assert.Equal(ToolCallDispatchStatus.Dispatched, before.Status); Assert.Null(before.FinishedAtUtc);
            child.Kill(entireProcessTree: true); await child.WaitForExitAsync(timeout.Token); Assert.NotEqual(0, child.ExitCode);
        }
        finally
        {
            if (!child.HasExited) { child.Kill(entireProcessTree: true); await child.WaitForExitAsync(); }
            Assert.True(string.IsNullOrEmpty(await errors), "Synthetic crash fixture wrote unexpected stderr.");
        }
        for (var restart = 0; restart < 2; restart++)
        {
            var probe = new Probe(runId);
            using var host = AppHost.CreateBuilder([], database.DatabasePath).ConfigureLogging(log => log.ClearProviders())
                .ConfigureServices(services => services.AddSingleton<IAgentRuntime>(probe)).Build();
            await host.StartAsync(timeout.Token);
            var runs = host.Services.GetRequiredService<TaskRunCoordinator>(); var calls = host.Services.GetRequiredService<IToolCallRepository>();
            var recovered = runs.GetRun(runId);
            Assert.Equal(RunStatus.Interrupted, recovered.Status); Assert.Equal("ToolOutcomeUnknown", recovered.ErrorCode);
            Assert.Contains("Verify its real-world result", recovered.Summary);
            var call = Assert.Single(await calls.ListAsync(runId)); Assert.Equal(ToolCallDispatchStatus.OutcomeUnknown, call.Status); Assert.NotNull(call.FinishedAtUtc);
            var approval = (await host.Services.GetRequiredService<IApprovalRepository>().GetAsync(call.ApprovalRequestId))!;
            var action = approval.Action;
            var conflict = await Assert.ThrowsAsync<ToolDispatchConflictException>(() => calls.BeginAsync(new(action.ToolId, "Replay forbidden", JsonSerializer.SerializeToElement(new { type = "object" }),
                    action.Source, action.RiskLevel) { Version = action.ToolVersion }, new(action.Arguments), new(runId, action.ConversationId, action.ToolCallId),
                recovered.RunVersion, approval.Id));
            Assert.True(conflict.OutcomeUnknown);
            var main = host.Services.GetRequiredService<MainWindow>(); var dashboard = host.Services.GetRequiredService<DashboardViewModel>();
            dashboard.SelectedHistory = dashboard.History.Single(value => value.Id == runId);
            await Until(() => dashboard.Status == "Interrupted" && !dashboard.IsActive);
            main.Measure(new Size(900, 800)); main.Arrange(new Rect(0, 0, 900, 800)); main.UpdateLayout(); await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Assert.Equal("Interrupted", ((TextBlock)main.FindName("RunStatusText")).Text);
            Assert.Contains("unknown outcome", ((TextBlock)main.FindName("RunSummaryText")).Text);
            Assert.False(dashboard.ResumeCommand.CanExecute(null));
            var sentinel = await runs.SubmitAsync(new(Guid.NewGuid(), "Fresh independent diagnostic", new("test")), timeout.Token);
            await runs.WaitForTerminalAsync(sentinel.Id, timeout.Token);
            Assert.Equal(0, probe.RecoveredCalls); Assert.Equal(["effect"], await File.ReadAllLinesAsync(effect, timeout.Token));
            main.Close(); await host.StopAsync(timeout.Token);
        }
        Assert.Equal(["effect"], await File.ReadAllLinesAsync(effect, timeout.Token));
    });
    private sealed class Probe(Guid recoveredId) : IAgentRuntime
    {
        public int RecoveredCalls;
        public Task<AgentRunResult> ExecuteAsync(AgentRequest request, IProgress<AgentEvent> progress, CancellationToken ct)
        {
            if (request.RunId == recoveredId) Interlocked.Increment(ref RecoveredCalls);
            return Task.FromResult(new AgentRunResult { Status = AgentResultStatus.Completed, Summary = "Fresh synthetic diagnostic complete" });
        }
    }
    private static async Task Until(Func<bool> condition) { using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(5)); while (!condition()) await Task.Delay(20, stop.Token); }
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
