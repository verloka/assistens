using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Protocol;
using PersonalAgent.Agent;
using PersonalAgent.Core.Agents;
using PersonalAgent.Core.Application;
using PersonalAgent.Core.Computer;
using PersonalAgent.Core.Domain;
using PersonalAgent.Core.Persistence;
using PersonalAgent.Core.Security;
using PersonalAgent.IntegrationTests.Fakes;
using PersonalAgent.Mcp;
using PersonalAgent.Persistence;
using System.Text.Json;

namespace PersonalAgent.IntegrationTests;

public sealed class HumanApprovalTests
{
    [Theory]
    [InlineData(false, false)] [InlineData(false, true)] [InlineData(true, false)] [InlineData(true, true)]
    public async Task ActualMafWaitsWithoutDesktopAndApproveExecutesOnceWhileRejectReturnsControlledDenial(bool mcp, bool approve)
    {
        await using var fixture = await Fixture.Start(mcp);
        var request = await fixture.Pending();
        Assert.Equal(0, fixture.Calls); Assert.Null(fixture.Desktop.Snapshot.OwnerRunId);
        Assert.Equal(RunStatus.WaitingForApproval, fixture.Runs.GetRun(fixture.RunId).Status);
        Assert.Equal(1, fixture.Client!.CallCount); Assert.Equal("synthetic-target", request.Action.Destination);
        Assert.Equal("sdk-exact-call", request.Action.ToolCallId);
        // Ordinary Resume cannot bypass an approval decision.
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Runs.ResumeAsync(new(Guid.NewGuid(), fixture.RunId, fixture.Runs.GetRun(fixture.RunId).RunVersion)));
        await fixture.Approvals.DecideAsync(Decision(request, approve));
        await fixture.UntilFinished();
        Assert.Equal(approve ? 1 : 0, fixture.Calls);
        var stored = (await fixture.Repository.GetAsync(fixture.RunId))!;
        Assert.Equal(approve ? RunStatus.Completed : RunStatus.Blocked, stored.Run.Status);
        Assert.Equal(approve ? null : "ToolPermissionDenied", stored.Result!.ErrorCode);
        Assert.Single(stored.Events, value => value.EventType == RunEventType.ApprovalRequested);
        Assert.Equal(approve ? 2 : 1, fixture.Client.CallCount);
        if (approve) Assert.Equal(ToolCallDispatchStatus.Acknowledged, Assert.Single(await fixture.ToolCalls.ListAsync(fixture.RunId)).Status);
        else Assert.Empty(await fixture.ToolCalls.ListAsync(fixture.RunId));
        await Assert.ThrowsAsync<ApprovalConflictException>(() => fixture.Approvals.DecideAsync(Decision(request, true)));
        Assert.Equal(approve ? 1 : 0, fixture.Calls);
    }

    [Theory]
    [InlineData("expired")] [InlineData("cancelled")] [InlineData("revoked")]
    public async Task ExpiryCancellationAndPolicyRevocationNeverExecuteOrRetry(string reason)
    {
        await using var fixture = await Fixture.Start(true); var request = await fixture.Pending();
        if (reason == "expired") fixture.Clock.Now += TimeSpan.FromMinutes(2);
        else if (reason == "cancelled")
            await fixture.Runs.CancelAsync(new(Guid.NewGuid(), fixture.RunId, fixture.Runs.GetRun(fixture.RunId).RunVersion));
        else { fixture.PolicyEnabled = false; await fixture.Approvals.DecideAsync(Decision(request, true)); }
        await fixture.UntilFinished(); Assert.Equal(0, fixture.Calls); Assert.Equal(1, fixture.Client!.CallCount);
        Assert.Null(fixture.Desktop.Snapshot.OwnerRunId);
        if (reason == "expired") Assert.Equal(ApprovalStatus.Expired, (await fixture.Approvals.GetAsync(request.Id))!.Status);
        if (reason == "cancelled") await Assert.ThrowsAsync<ApprovalConflictException>(() => fixture.Approvals.DecideAsync(Decision(request, true)));
    }

    [Fact]
    public async Task OwnerPauseInvalidatesPendingActionAndCannotBeAutomaticallyResumedByApprovalWait()
    {
        await using var fixture = await Fixture.Start(true); var request = await fixture.Pending();
        await fixture.Runs.PauseAsync(new(Guid.NewGuid(), fixture.RunId, fixture.Runs.GetRun(fixture.RunId).RunVersion));
        await Assert.ThrowsAsync<ApprovalConflictException>(() => fixture.Approvals.DecideAsync(Decision(request, true)));
        await Until(() => fixture.Runs.GetRun(fixture.RunId).Status == RunStatus.Paused);
        Assert.Equal(0, fixture.Calls); Assert.Null(fixture.Desktop.Snapshot.OwnerRunId);
        Assert.Equal(RunStatus.Paused, fixture.Runs.GetRun(fixture.RunId).Status);
        await fixture.Runs.ResumeAsync(new(Guid.NewGuid(), fixture.RunId, fixture.Runs.GetRun(fixture.RunId).RunVersion));
        await fixture.UntilFinished(); Assert.Equal(RunStatus.Blocked, fixture.Runs.GetRun(fixture.RunId).Status); Assert.Equal(0, fixture.Calls);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task ExpiryOrPauseAfterApprovalButBeforeDispatchInvalidatesSingleUseReceipt(bool pause)
    {
        await using var fixture = await Fixture.Start(true); fixture.HoldDispatch = true;
        var request = await fixture.Pending(); await fixture.Approvals.DecideAsync(Decision(request, true));
        await fixture.DispatchEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(0, fixture.Calls);
        if (pause)
        {
            await fixture.Runs.PauseAsync(new(Guid.NewGuid(), fixture.RunId, fixture.Runs.GetRun(fixture.RunId).RunVersion));
            var boundary = fixture.Runs.WaitAtBoundaryAsync(fixture.RunId, default(CancellationToken));
            await Until(() => fixture.Runs.GetRun(fixture.RunId).Status == RunStatus.Paused);
            await fixture.Runs.ResumeAsync(new(Guid.NewGuid(), fixture.RunId, fixture.Runs.GetRun(fixture.RunId).RunVersion));
            await boundary;
        }
        else fixture.Clock.Now += TimeSpan.FromMinutes(2);
        fixture.DispatchRelease.TrySetResult(); await fixture.UntilFinished();
        Assert.Equal(RunStatus.Blocked, fixture.Runs.GetRun(fixture.RunId).Status); Assert.Equal(0, fixture.Calls);
        Assert.Equal("ToolPermissionDenied", (await fixture.Repository.GetAsync(fixture.RunId))!.Result!.ErrorCode);
    }

    private static DecideApprovalCommand Decision(ApprovalRequest request, bool approve) => new(request.Id, request.Version, request.ActionHash, Fixture.Owner, approve);
    private static async Task Until(Func<bool> condition)
    { using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10)); while (!condition()) await Task.Delay(20, timeout.Token); }

    internal sealed class Fixture : IAsyncDisposable
    {
        public static readonly ApprovalOwner Owner = new("desktop", "synthetic-owner");
        public readonly TemporaryAgentDatabase Database = new();
        public readonly DesktopAccessCoordinator Desktop = new(_ => true);
        public readonly TestClock Clock = new();
        public IRunRepository Repository = null!; public IApprovalRepository Approvals = null!;
        public IToolCallRepository ToolCalls = null!;
        public TaskRunCoordinator Runs = null!; public ScriptedChatClient? Client;
        public Guid RunId; public int Calls; public bool PolicyEnabled = true;
        public bool HoldDispatch;
        public TaskCompletionSource DispatchEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource DispatchRelease = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly CancellationTokenSource stop = new();
        private Task runner = null!; private WebApplication server = null!; private McpClientConnection connection = null!;
        private IAgentTool tool = null!; private McpToolPermission permission = null!;
        private DesktopLease? lease;

        public static async Task<Fixture> Start(bool mcp, Func<IToolCallRepository, IToolCallRepository>? decorateLedger = null, bool loseMcpReply = false)
        {
            var fixture = new Fixture();
            await using (var db = fixture.Database.Open()) await db.Database.MigrateAsync();
            fixture.Repository = new SqliteRunRepository(fixture.Database); fixture.Approvals = new SqliteApprovalRepository(fixture.Database, fixture.Clock);
            fixture.ToolCalls = new SqliteToolCallRepository(fixture.Database, fixture.Clock);
            if (decorateLedger is not null) fixture.ToolCalls = decorateLedger(fixture.ToolCalls);
            fixture.Runs = new(fixture.Repository, fixture.Desktop);
            fixture.server = McpTestServer.Program.CreateHttpServer(listTools: () => [new Tool { Name = "write_issue", Description = "Synthetic write only",
                InputSchema = Schema() }], callTool: async (_, token) =>
                {
                    Interlocked.Increment(ref fixture.Calls);
                    if (loseMcpReply) await Task.Delay(Timeout.InfiniteTimeSpan, token);
                    return new CallToolResult { Content = [new TextContentBlock { Text = "Synthetic write acknowledged" }] };
                });
            await fixture.server.StartAsync();
            var endpoint = new Uri(Assert.Single(fixture.server.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses) + "/mcp");
            fixture.connection = await new McpConnectionFactory([new HttpMcpTransportProvider(new Secrets())]).ConnectAsync(new()
            { Id = Guid.NewGuid(), Name = "Fixture", Enabled = true, Approved = true, Transport = McpTransportKind.StreamableHttp,
                Http = new(endpoint, AllowLoopbackHttp: true), AllowedTools = ["write_issue"], RequestTimeout = TimeSpan.FromSeconds(loseMcpReply ? 1 : 10) });
            var declaration = Assert.Single((await new McpToolDiscovery().RefreshAsync(fixture.connection)).Tools);
            fixture.permission = new(fixture.connection.Configuration.Id, declaration.Descriptor.Id, declaration.Version, ToolRiskLevel.Write);
            fixture.tool = mcp ? new McpAgentTool(fixture.connection, declaration, fixture.permission) : new LocalWrite(fixture);
            var run = await fixture.Runs.SubmitAsync(new(Guid.NewGuid(), "Synthetic approval fixture", new("desktop", Owner.UserId), ModelProfileId: "fixture",
                AllowedToolIds: [fixture.tool.Descriptor.Id])); fixture.RunId = run.Id;
            fixture.runner = new TaskRunner(fixture.Runs, new Runtime(fixture.Execute)).RunAsync(fixture.stop.Token);
            return fixture;
        }

        private async Task<AgentRunResult> Execute(AgentRequest request, IProgress<AgentEvent> progress, CancellationToken ct)
        {
            lease = await Desktop.AcquireAsync(request.RunId, ct); // Synthetic earlier GUI ownership; no native operation.
            var access = new ToolAccessContext(request.RunId, request.ConversationId, [ToolRiskLevel.Write]);
            var descriptor = tool.Descriptor;
            var policy = new ToolPermissionPolicy(access, [new(descriptor.Id, descriptor.Source, descriptor.RiskLevel, ToolPermissionEffect.Allow, descriptor.Version)],
                descriptor.Source.Kind == ToolSourceKind.Mcp ? [permission] : []);
            ToolPermissionDecision Evaluate(ToolDescriptor declaration, ToolExecutionContext context) => PolicyEnabled ? policy.Evaluate(declaration, context) : ToolPermissionDecision.Denied;
            var human = new HumanApprovalAuthorizer(Runs, Approvals, Evaluate, Owner, (_, args) => ("Write the synthetic fixture", args.Value.GetProperty("target").GetString()!),
                Desktop, async token => { if (lease is not null) await Desktop.ReleaseAsync(lease, token); lease = null; }, TimeSpan.FromMinutes(1), Clock);
            var boundaries = 0;
            var registry = new ToolRegistry([tool], async (id, step, token) =>
            {
                if (++boundaries == 2 && HoldDispatch)
                { DispatchEntered.TrySetResult(); await DispatchRelease.Task.WaitAsync(token); }
                await Runs.WaitAtBoundaryAsync(id, step, token);
            }, (declaration, context, _) => ValueTask.FromResult(Evaluate(declaration, context) != ToolPermissionDecision.Denied), access, human, ToolCalls);
            Client = new((_, _) => new(new ChatMessage(ChatRole.Assistant, [new FunctionCallContent("sdk-exact-call", descriptor.Id,
                new Dictionary<string, object?> { ["target"] = "synthetic-target" })])) { FinishReason = ChatFinishReason.ToolCalls },
                (messages, _) => { Assert.Contains(messages.SelectMany(value => value.Contents).OfType<FunctionResultContent>(), value => value.CallId == "sdk-exact-call");
                    return new(new ChatMessage(ChatRole.Assistant, "Synthetic approved action complete")) { FinishReason = ChatFinishReason.Stop }; });
            return await new MafAgentRuntime((_, _) => ValueTask.FromResult<IChatClient>(Client), Runs.WaitAtBoundaryAsync, registry).ExecuteAsync(request, progress, ct);
        }
        public async Task<ApprovalRequest> Pending()
        {
            ApprovalRequest? pending = null; using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            while (pending is null) { pending = (await Approvals.ListPendingAsync(Owner, timeout.Token)).SingleOrDefault(); if (pending is null) await Task.Delay(20, timeout.Token); }
            return pending;
        }
        public Task UntilFinished() => Until(() => Runs.GetRun(RunId).Status is RunStatus.Completed or RunStatus.Blocked or RunStatus.Cancelled);
        public async ValueTask DisposeAsync()
        {
            stop.Cancel(); await runner; Runs.Dispose(); stop.Dispose();
            if (lease is not null) await Desktop.ReleaseAsync(lease);
            await connection.DisposeAsync(); await server.StopAsync(); await server.DisposeAsync(); Database.Dispose();
        }
        private sealed class LocalWrite(Fixture fixture) : IAgentTool
        {
            public ToolDescriptor Descriptor { get; } = new("fixture_write", "Synthetic local write", Schema(), new(ToolSourceKind.Local, "fixture"), ToolRiskLevel.Write);
            public Task<ToolResult> ExecuteAsync(ToolArguments arguments, ToolExecutionContext context, CancellationToken ct)
            { Interlocked.Increment(ref fixture.Calls); return Task.FromResult(new ToolResult { Status = ToolResultStatus.Success, Text = "Synthetic write acknowledged" }); }
        }
        private static JsonElement Schema() => JsonSerializer.SerializeToElement(new { type = "object", properties = new { target = new { type = "string", @const = "synthetic-target" } },
            required = new[] { "target" }, additionalProperties = false });
    }
    internal sealed class TestClock : TimeProvider { public DateTimeOffset Now = DateTimeOffset.UtcNow; public override DateTimeOffset GetUtcNow() => Now; }
    private sealed class Secrets : ISecretStore
    {
        public ValueTask<string> StoreAsync(string secret, CancellationToken ct = default) => throw new NotSupportedException();
        public ValueTask<string?> ReadAsync(string reference, CancellationToken ct = default) => throw new NotSupportedException();
    }
    private sealed class Runtime(Func<AgentRequest, IProgress<AgentEvent>, CancellationToken, Task<AgentRunResult>> execute) : IAgentRuntime
    { public Task<AgentRunResult> ExecuteAsync(AgentRequest request, IProgress<AgentEvent> progress, CancellationToken ct) => execute(request, progress, ct); }
}
