using PersonalAgent.Core.Agents;
using PersonalAgent.Core.Application;
using PersonalAgent.Core.Domain;
using PersonalAgent.Core.Persistence;
using PersonalAgent.Core.Security;
using System.Text.Json;

namespace PersonalAgent.IntegrationTests;

public sealed class CommonToolPolicyTests
{
    [Theory]
    [InlineData(false, "approve")] [InlineData(true, "approve")]
    [InlineData(false, "reject")] [InlineData(true, "reject")]
    [InlineData(false, "expire")] [InlineData(true, "expire")]
    [InlineData(false, "cancel")] [InlineData(true, "cancel")]
    public async Task SameExactOwnerPolicyControlsRealMcpAndControllerBackedSyntheticGui(bool computer, string decision)
    {
        await using var fixture = await HumanApprovalTests.Fixture.Start(!computer, computer: computer);
        var request = await fixture.Pending();
        Assert.Equal(computer ? ToolSourceKind.Computer : ToolSourceKind.Mcp, request.Action.Source.Kind);
        Assert.Equal(ToolRiskLevel.Write, request.Action.RiskLevel); Assert.Equal(0, fixture.Calls);
        Assert.Null(fixture.Desktop.Snapshot.OwnerRunId); Assert.Equal(0, fixture.Native.Captures); Assert.Empty(fixture.Native.Inputs);
        var command = new DecideApprovalCommand(request.Id, request.Version, request.ActionHash, HumanApprovalTests.Fixture.Owner, true);
        await Assert.ThrowsAsync<ApprovalConflictException>(() => fixture.Approvals.DecideAsync(command with { Owner = command.Owner with { UserId = "other" } }));
        await Assert.ThrowsAsync<ApprovalConflictException>(() => fixture.Approvals.DecideAsync(command with
        { ActionHash = (request.Action with { Arguments = JsonSerializer.SerializeToElement(new { target = "different" }) }).Fingerprint() }));
        Assert.Equal(0, fixture.Calls); Assert.Empty(fixture.Native.Inputs);
        if (decision is "approve" or "reject") await fixture.Approvals.DecideAsync(command with { Approve = decision == "approve" });
        else if (decision == "expire") fixture.Clock.Now += TimeSpan.FromMinutes(2);
        else await fixture.Runs.CancelAsync(new(Guid.NewGuid(), fixture.RunId, fixture.Runs.GetRun(fixture.RunId).RunVersion));
        await fixture.UntilFinished();
        Assert.Equal(decision == "approve" ? 1 : 0, fixture.Calls); Assert.Null(fixture.Desktop.Snapshot.OwnerRunId);
        if (computer && decision == "approve")
        { Assert.Equal(1, fixture.Native.Captures); Assert.Single(fixture.Native.Inputs); Assert.True(fixture.Desktop.Snapshot.Generation >= 2); }
        else { Assert.Equal(0, fixture.Native.Captures); Assert.Empty(fixture.Native.Inputs); }
        var calls = await fixture.ToolCalls.ListAsync(fixture.RunId);
        if (decision == "approve") Assert.Equal(ToolCallDispatchStatus.Acknowledged, Assert.Single(calls).Status);
        else Assert.Empty(calls);
        await Assert.ThrowsAsync<ApprovalConflictException>(() => fixture.Approvals.DecideAsync(command));
        Assert.Equal(decision == "approve" ? 1 : 0, fixture.Calls);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task UnknownWriteRiskNeverReachesApprovalDispatchOrEitherEffectAdapter(bool computer)
    {
        await using var fixture = await HumanApprovalTests.Fixture.Start(!computer, computer: computer, unknownRisk: true);
        await fixture.UntilFinished();
        Assert.Equal(RunStatus.Blocked, fixture.Runs.GetRun(fixture.RunId).Status);
        Assert.Equal("ToolPermissionDenied", (await fixture.Repository.GetAsync(fixture.RunId))!.Result!.ErrorCode);
        Assert.Empty(await fixture.Approvals.ListPendingAsync(HumanApprovalTests.Fixture.Owner));
        Assert.Empty(await fixture.ToolCalls.ListAsync(fixture.RunId)); Assert.Equal(0, fixture.Calls);
        Assert.Equal(0, fixture.Native.Captures); Assert.Empty(fixture.Native.Inputs); Assert.Null(fixture.Desktop.Snapshot.OwnerRunId);
        Assert.Equal(1, fixture.Client!.CallCount);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task OwnerApprovalRacingCancelNeverDuplicatesEitherEffectOrUsesUnapprovedPayload(bool computer)
    {
        await using var fixture = await HumanApprovalTests.Fixture.Start(!computer, computer: computer); var request = await fixture.Pending();
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var approval = Task.Run(async () =>
        {
            await start.Task;
            try { await fixture.Approvals.DecideAsync(new(request.Id, request.Version, request.ActionHash, HumanApprovalTests.Fixture.Owner, true)); }
            catch (ApprovalConflictException) { }
        });
        var cancel = Task.Run(async () =>
        {
            await start.Task;
            try { await fixture.Runs.CancelAsync(new(Guid.NewGuid(), fixture.RunId, fixture.Runs.GetRun(fixture.RunId).RunVersion)); }
            catch (RunVersionConflictException) { } // The approved operation may already have crossed its dispatch boundary.
        });
        start.SetResult(); await Task.WhenAll(approval, cancel); await fixture.UntilFinished();
        Assert.InRange(fixture.Calls, 0, 1); Assert.InRange((await fixture.ToolCalls.ListAsync(fixture.RunId)).Count, 0, 1);
        Assert.Null(fixture.Desktop.Snapshot.OwnerRunId);
        if (fixture.Calls != 0 || fixture.Native.Inputs.Count != 0)
            Assert.Equal(ApprovalStatus.Approved, (await fixture.Approvals.GetAsync(request.Id))!.Status);
        Assert.Contains(fixture.Runs.GetRun(fixture.RunId).Status, new[] { RunStatus.Cancelled, RunStatus.Completed });
    }
}
