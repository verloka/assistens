using Microsoft.EntityFrameworkCore;
using PersonalAgent.Core.Agents;
using PersonalAgent.Core.Domain;
using PersonalAgent.Core.Persistence;
using PersonalAgent.Core.Security;
using PersonalAgent.IntegrationTests.Fakes;
using PersonalAgent.Persistence;
using System.Text.Json;

namespace PersonalAgent.IntegrationTests;

public sealed class ToolDispatchTests
{
    [Fact]
    public async Task ConcurrentReservationsHaveOneWinnerAndAcknowledgedCallOrApprovalCannotBeReused()
    {
        using var database = new TemporaryAgentDatabase(); var (approval, version) = await Ready(database);
        var action = approval.Action; var tool = Descriptor(action); var context = Context(action);
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var attempts = Enumerable.Range(0, 8).Select(_ => Task.Run(async () =>
        {
            await start.Task;
            try { return await new SqliteToolCallRepository(database).BeginAsync(tool, new(action.Arguments), context, version, approval.Id); }
            catch (ToolDispatchConflictException) { return null; }
        })).ToArray();
        start.SetResult(); var winner = Assert.Single(await Task.WhenAll(attempts), value => value is not null)!;
        var calls = new SqliteToolCallRepository(database);
        Assert.Equal(ToolCallDispatchStatus.Dispatched, Assert.Single(await calls.ListAsync(action.RunId)).Status);
        await calls.CompleteAsync(winner.Id, ToolResultStatus.Success);
        var reopened = new SqliteToolCallRepository(new SqliteAgentDbContextFactory(database.DatabasePath));
        Assert.Equal(ToolCallDispatchStatus.Acknowledged, Assert.Single(await reopened.ListAsync(action.RunId)).Status);
        var conflict = await Assert.ThrowsAsync<ToolDispatchConflictException>(() => calls.BeginAsync(tool, new(action.Arguments), context, version, approval.Id));
        Assert.False(conflict.OutcomeUnknown);
        await Assert.ThrowsAsync<ToolDispatchDeniedException>(() => calls.BeginAsync(tool, new(action.Arguments), context with { ToolCallId = "other-call" }, version, approval.Id));
        await Assert.ThrowsAsync<ToolDispatchConflictException>(() => calls.CompleteAsync(winner.Id, ToolResultStatus.Failed));
        Assert.Equal(ToolCallDispatchStatus.Acknowledged, Assert.Single(await calls.ListAsync(action.RunId)).Status);
    }

    [Fact]
    public async Task ExactPayloadToolSourceRiskRunAndRevisionAreRecheckedBeforeReservation()
    {
        using var database = new TemporaryAgentDatabase(); var (approval, version) = await Ready(database);
        var action = approval.Action; var tool = Descriptor(action); var context = Context(action); var calls = new SqliteToolCallRepository(database);
        foreach (var changed in new[] { tool with { Id = "different_tool" }, tool with { RiskLevel = ToolRiskLevel.Destructive },
            tool with { Source = new(ToolSourceKind.Local, "different-source") }, tool with { Version = "different-version" } })
            await Assert.ThrowsAsync<ToolDispatchDeniedException>(() => calls.BeginAsync(changed, new(action.Arguments), context, version, approval.Id));
        await Assert.ThrowsAsync<ToolDispatchDeniedException>(() => calls.BeginAsync(tool, new(JsonSerializer.SerializeToElement(new { target = "attacker" })), context, version, approval.Id));
        await Assert.ThrowsAsync<ToolDispatchDeniedException>(() => calls.BeginAsync(tool, new(action.Arguments), context with { ConversationId = Guid.NewGuid() }, version, approval.Id));
        await Assert.ThrowsAsync<ToolDispatchDeniedException>(() => calls.BeginAsync(tool, new(action.Arguments), context, version + 1, approval.Id));
        await Assert.ThrowsAsync<ToolDispatchDeniedException>(() => calls.BeginAsync(tool, new(action.Arguments), context, version, Guid.NewGuid()));
        Assert.Empty(await calls.ListAsync(action.RunId));
    }

    [Theory]
    [InlineData("pending")] [InlineData("rejected")] [InlineData("expired")] [InlineData("cancelled")]
    public async Task UnapprovedExpiredAndCancelledActionsCannotReserveDispatch(string reason)
    {
        using var database = new TemporaryAgentDatabase(); var (approval, version) = await Ready(database, reason == "pending" ? null : reason != "rejected");
        var action = approval.Action; var calls = new SqliteToolCallRepository(database, reason == "expired" ? new FutureClock() : null);
        if (reason == "cancelled")
        {
            var repository = new SqliteRunRepository(database); var stored = (await repository.GetAsync(action.RunId))!;
            var cancelled = TaskRunStateMachine.Transition(stored.Run, RunStatus.Cancelled, version, DateTimeOffset.UtcNow);
            await repository.UpdateAsync(cancelled, version, [Event(action.RunId, stored.Events.Count + 1)]);
        }
        await Assert.ThrowsAsync<ToolDispatchDeniedException>(() => calls.BeginAsync(Descriptor(action), new(action.Arguments), Context(action), version, approval.Id));
        Assert.Empty(await calls.ListAsync(action.RunId));
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task ReservationFailurePreventsEffectAndLostAckPersistenceSurfacesUnknownWithoutRetry(bool afterEffect)
    {
        await using var fixture = await HumanApprovalTests.Fixture.Start(true, inner => new FaultingLedger(inner, afterEffect));
        var request = await fixture.Pending();
        await fixture.Approvals.DecideAsync(new(request.Id, request.Version, request.ActionHash, HumanApprovalTests.Fixture.Owner, true));
        await fixture.UntilFinished();
        var stored = (await fixture.Repository.GetAsync(fixture.RunId))!;
        Assert.Equal(afterEffect ? 1 : 0, fixture.Calls); Assert.Equal(1, fixture.Client!.CallCount);
        Assert.Equal(afterEffect ? "ToolOutcomeUnknown" : "ToolDispatchSaveFailed", stored.Result!.ErrorCode);
        Assert.DoesNotContain("private-ledger-error", JsonSerializer.Serialize(stored.Result));
        var calls = new SqliteToolCallRepository(fixture.Database);
        if (afterEffect)
        {
            Assert.Equal(ToolCallDispatchStatus.Dispatched, Assert.Single(await calls.ListAsync(fixture.RunId)).Status);
            await calls.RecoverDispatchedAsync(); Assert.Equal(ToolCallDispatchStatus.OutcomeUnknown, Assert.Single(await calls.ListAsync(fixture.RunId)).Status);
        }
        else Assert.Empty(await calls.ListAsync(fixture.RunId));
    }

    [Fact]
    public async Task ActualMcpEffectWithoutResponseIsDurablyUnknownAndNeverReplayed()
    {
        await using var fixture = await HumanApprovalTests.Fixture.Start(true, loseMcpReply: true); var request = await fixture.Pending();
        await fixture.Approvals.DecideAsync(new(request.Id, request.Version, request.ActionHash, HumanApprovalTests.Fixture.Owner, true));
        await fixture.UntilFinished();
        var stored = (await fixture.Repository.GetAsync(fixture.RunId))!;
        Assert.Equal(1, fixture.Calls); Assert.Equal(1, fixture.Client!.CallCount); Assert.Equal("McpRequestTimedOut", stored.Result!.ErrorCode);
        Assert.Contains("Verify its real-world state", stored.Result.Summary);
        var call = Assert.Single(await fixture.ToolCalls.ListAsync(fixture.RunId)); Assert.Equal(ToolCallDispatchStatus.OutcomeUnknown, call.Status);
        var conflict = await Assert.ThrowsAsync<ToolDispatchConflictException>(() => fixture.ToolCalls.BeginAsync(Descriptor(request.Action),
            new(request.Action.Arguments), Context(request.Action), fixture.Runs.GetRun(fixture.RunId).RunVersion, request.Id));
        Assert.True(conflict.OutcomeUnknown); Assert.Equal(1, fixture.Calls);
    }

    private static async Task<(ApprovalRequest Request, long RunVersion)> Ready(TemporaryAgentDatabase database, bool? approve = true)
    {
        var action = await ApprovalRepositoryTests.Seed(database); var approvals = new SqliteApprovalRepository(database);
        var request = await approvals.CreateAsync(action, new("desktop", "synthetic-owner"), TimeSpan.FromMinutes(1));
        if (approve is { } decision) request = await approvals.DecideAsync(new(request.Id, request.Version, request.ActionHash, request.Owner, decision));
        var repository = new SqliteRunRepository(database); var stored = (await repository.GetAsync(action.RunId))!;
        var running = TaskRunStateMachine.Transition(stored.Run, RunStatus.Running, stored.Run.RunVersion, DateTimeOffset.UtcNow);
        await repository.UpdateAsync(running, stored.Run.RunVersion, [Event(action.RunId, stored.Events.Count + 1)]);
        return (request, running.RunVersion);
    }
    private static ToolExecutionContext Context(PendingToolAction action) => new(action.RunId, action.ConversationId, action.ToolCallId);
    private static ToolDescriptor Descriptor(PendingToolAction action) => new(action.ToolId, "Synthetic dispatch", JsonSerializer.SerializeToElement(new { type = "object" }),
        action.Source, action.RiskLevel) { Version = action.ToolVersion };
    private static RunEvent Event(Guid runId, long sequence) => new() { Id = Guid.NewGuid(), RunId = runId, Sequence = sequence,
        EventType = RunEventType.StatusChanged, TimestampUtc = DateTimeOffset.UtcNow, Message = "Synthetic dispatch fixture" };
    private sealed class FutureClock : TimeProvider { public override DateTimeOffset GetUtcNow() => DateTimeOffset.UtcNow.AddMinutes(2); }
    private sealed class FaultingLedger(IToolCallRepository inner, bool afterEffect) : IToolCallRepository
    {
        public Task<ToolCallDispatch> BeginAsync(ToolDescriptor tool, ToolArguments arguments, ToolExecutionContext invocation, long version, Guid approvalId, CancellationToken ct = default)
            => afterEffect ? inner.BeginAsync(tool, arguments, invocation, version, approvalId, ct) : throw new InvalidOperationException("private-ledger-error");
        public Task CompleteAsync(Guid id, ToolResultStatus result, CancellationToken ct = default) => throw new InvalidOperationException("private-ledger-error");
        public Task<IReadOnlyList<ToolCallDispatch>> ListAsync(Guid runId, CancellationToken ct = default) => inner.ListAsync(runId, ct);
        public Task RecoverDispatchedAsync(CancellationToken ct = default) => inner.RecoverDispatchedAsync(ct);
    }
}
