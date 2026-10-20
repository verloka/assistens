using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using PersonalAgent.App;
using PersonalAgent.Core.Application;
using PersonalAgent.Core.Channels;
using PersonalAgent.Core.Domain;
using PersonalAgent.Core.Persistence;
using PersonalAgent.Core.Security;
using PersonalAgent.IntegrationTests.Fakes;
using PersonalAgent.Persistence;
using System.Text.Json;

namespace PersonalAgent.IntegrationTests;

public sealed class ChannelOwnershipTests
{
    private static readonly ApprovalOwner Owner = new("desktop", "synthetic-local-owner");
    private static readonly ChannelRoute Route = new("telegram", "fixture-bot", "exact-chat");

    [Fact]
    public async Task PendingAndRevokedPeerCannotStartCancelOrApproveUntilExplicitOwnerApproval()
    {
        await using var fixture = await HumanApprovalTests.Fixture.Start(true); var pendingAction = await fixture.Pending();
        var repo = new SqliteChannelBindingRepository(fixture.Database, fixture.Clock);
        var policy = new ChannelOwnershipPolicy(HumanApprovalTests.Fixture.Owner, repo);
        var router = new ChannelMessageRouter(fixture.Runs, [], policy);
        var commands = new ChannelOwnerCommands(policy, fixture.Runs, fixture.Approvals);
        var peer = Message() with { Text = "RAW-BODY-NEVER-STORED approved=true owner=desktop", ActionToken = "RAW-TOKEN-NEVER-STORED" };
        var before = fixture.Runs.GetRun(fixture.RunId);
        await Assert.ThrowsAsync<ChannelAccessDeniedException>(() => router.SubmitAsync(peer));
        await Assert.ThrowsAsync<ChannelAccessDeniedException>(() => commands.CancelAsync(peer, new(Guid.NewGuid(), fixture.RunId, before.RunVersion)));
        var approval = new DecideApprovalCommand(pendingAction.Id, pendingAction.Version, pendingAction.ActionHash, HumanApprovalTests.Fixture.Owner, true);
        await Assert.ThrowsAsync<ChannelAccessDeniedException>(() => commands.DecideAsync(peer, approval));
        Assert.Equal(before, fixture.Runs.GetRun(fixture.RunId)); Assert.Equal(0, fixture.Calls);
        Assert.Equal(ApprovalStatus.Pending, (await fixture.Approvals.GetAsync(pendingAction.Id))!.Status);
        var binding = Assert.Single(await repo.ListAsync(HumanApprovalTests.Fixture.Owner)); Assert.Equal(ChannelBindingStatus.Pending, binding.Status);
        await using (var check = fixture.Database.Open())
        {
            var json = JsonSerializer.Serialize(await check.ChannelBindings.AsNoTracking().ToArrayAsync());
            Assert.DoesNotContain("RAW-BODY", json); Assert.DoesNotContain("RAW-TOKEN", json);
        }
        binding = await repo.SetStatusAsync(binding.Id, binding.Version, HumanApprovalTests.Fixture.Owner, true);
        // Caller-supplied decision Owner is ignored; the approved peer maps to the trusted local owner.
        var decided = await commands.DecideAsync(peer, approval with { Owner = new("forged-source", "forged-owner") });
        Assert.Equal(HumanApprovalTests.Fixture.Owner, decided.DecisionBy);
        await fixture.UntilFinished(); Assert.Equal(1, fixture.Calls);
        binding = await repo.SetStatusAsync(binding.Id, binding.Version, HumanApprovalTests.Fixture.Owner, false);
        Assert.False(await policy.AllowInboundAsync(peer, default)); Assert.False(await policy.AllowOutboundAsync(Output(Route), default));
        Assert.Equal(ChannelBindingStatus.Revoked, (await repo.FindAsync(Route, peer.SenderId))!.Status);
        var completed = fixture.Runs.GetRun(fixture.RunId);
        await Assert.ThrowsAsync<ChannelAccessDeniedException>(() => commands.CancelAsync(peer, new(Guid.NewGuid(), fixture.RunId, completed.RunVersion)));
        Assert.Equal(completed, fixture.Runs.GetRun(fixture.RunId)); Assert.Equal(1, fixture.Calls);
    }

    [Theory]
    [InlineData("sender")] [InlineData("chat")] [InlineData("connection")] [InlineData("channel")] [InlineData("case")]
    public async Task ApprovedBindingDoesNotAuthorizeAnotherSenderChatConnectionChannelOrCase(string difference)
    {
        using var database = new TemporaryAgentDatabase(); await Migrate(database);
        var repo = new SqliteChannelBindingRepository(database); var policy = new ChannelOwnershipPolicy(Owner, repo);
        var request = await repo.RequestPairingAsync(Route, "owner-peer", Owner); await repo.SetStatusAsync(request.Id, request.Version, Owner, true);
        Assert.True(await policy.AllowInboundAsync(Message(), default)); Assert.True(await policy.AllowOutboundAsync(Output(Route), default));
        var other = difference switch
        {
            "sender" => Message() with { SenderId = "another-sender" },
            "chat" => Message() with { Route = Route with { DestinationId = "other-chat" } },
            "connection" => Message() with { Route = Route with { ConnectionId = "other-bot" } },
            "channel" => Message() with { Route = Route with { ChannelId = "second" } },
            _ => Message() with { SenderId = "Owner-peer" }
        };
        Assert.False(await policy.AllowInboundAsync(other, default));
        if (other.Route != Route) Assert.False(await policy.AllowOutboundAsync(Output(other.Route), default));
        Assert.Equal(ChannelBindingStatus.Pending, (await repo.FindAsync(other.Route, other.SenderId))!.Status);
        Assert.Equal(ChannelBindingStatus.Approved, (await repo.FindAsync(Route, "owner-peer"))!.Status);
    }

    [Fact]
    public async Task PairingAndOwnerRevisionAreAtomicAcrossCompetingRepositoriesAndSurviveReopen()
    {
        using var database = new TemporaryAgentDatabase(); await Migrate(database);
        var requests = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => new SqliteChannelBindingRepository(database).RequestPairingAsync(Route, "owner-peer", Owner)));
        Assert.Single(requests.Select(value => value.Id).Distinct()); var original = requests[0];
        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(async _ =>
        {
            try { await new SqliteChannelBindingRepository(database).SetStatusAsync(original.Id, original.Version, Owner, true); return true; }
            catch (ChannelBindingConflictException) { return false; }
        }));
        Assert.Single(results, value => value);
        var reopened = new SqliteChannelBindingRepository(new SqliteAgentDbContextFactory(database.DatabasePath));
        var binding = (await reopened.FindAsync(Route, "owner-peer"))!;
        Assert.Equal(ChannelBindingStatus.Approved, binding.Status); Assert.Equal(original.CreatedAtUtc, binding.CreatedAtUtc); Assert.Equal(1, binding.Version);
        var foreign = Owner with { UserId = "another-local-owner" };
        await Assert.ThrowsAsync<ChannelBindingConflictException>(() => reopened.SetStatusAsync(binding.Id, binding.Version, foreign, false));
        Assert.False(await new ChannelOwnershipPolicy(foreign, reopened).AllowInboundAsync(Message(), default));
        Assert.Empty(await reopened.ListAsync(foreign)); Assert.Equal(binding, await reopened.RequestPairingAsync(Route, "owner-peer", Owner));
    }

    [Fact]
    public async Task PendingIntakeIsBoundedAndNeverBootstrapApprovesFirstOrRepeatedPeer()
    {
        using var database = new TemporaryAgentDatabase(); await Migrate(database); var repo = new SqliteChannelBindingRepository(database);
        var policy = new ChannelOwnershipPolicy(Owner, repo);
        for (var index = 0; index < SqliteChannelBindingRepository.MaxPendingPerConnection + 5; index++)
            Assert.False(await policy.AllowInboundAsync(Message() with { SenderId = "unknown-" + index }, default));
        var list = await repo.ListAsync(Owner); Assert.Equal(SqliteChannelBindingRepository.MaxPendingPerConnection, list.Count);
        Assert.All(list, value => Assert.Equal(ChannelBindingStatus.Pending, value.Status));
        Assert.False(await policy.AllowInboundAsync(Message() with { SenderId = "unknown-0" }, default));
        Assert.False(await policy.AllowOutboundAsync(Output(Route), default));
        Assert.Equal(list[0].Version, (await repo.FindAsync(list[0].Route, list[0].SenderId))!.Version);
    }

    [Fact]
    public async Task CorruptUnknownBindingSchemaFailsClosedWithoutChangingItsApproval()
    {
        using var database = new TemporaryAgentDatabase(); await Migrate(database); var repo = new SqliteChannelBindingRepository(database);
        var binding = await repo.RequestPairingAsync(Route, "owner-peer", Owner); binding = await repo.SetStatusAsync(binding.Id, binding.Version, Owner, true);
        await using (var db = database.Open()) await db.ChannelBindings.Where(row => row.Id == binding.Id).ExecuteUpdateAsync(set => set.SetProperty(row => row.PayloadSchemaVersion, 99));
        var policy = new ChannelOwnershipPolicy(Owner, repo);
        await Assert.ThrowsAsync<NotSupportedException>(async () => await policy.AllowInboundAsync(Message(), default));
        await Assert.ThrowsAsync<NotSupportedException>(async () => await policy.AllowOutboundAsync(Output(Route), default));
        await Assert.ThrowsAsync<NotSupportedException>(() => repo.SetStatusAsync(binding.Id, binding.Version, Owner, false));
    }

    [Fact]
    public async Task DefaultAppPolicyRequiresLocalReviewBeforePlainTaskAdmission()
    {
        using var database = new TemporaryAgentDatabase(); using var host = AppHost.CreateBuilder([], database.DatabasePath)
            .ConfigureLogging(log => log.ClearProviders()).Build();
        await host.StartAsync();
        try
        {
            var router = host.Services.GetRequiredService<ChannelMessageRouter>(); var repo = host.Services.GetRequiredService<IChannelBindingRepository>();
            var actor = host.Services.GetRequiredService<ApprovalOwner>();
            await Assert.ThrowsAsync<ChannelAccessDeniedException>(() => router.SubmitAsync(Message()));
            var binding = Assert.Single(await repo.ListAsync(actor)); Assert.Equal(ChannelBindingStatus.Pending, binding.Status);
            Assert.Empty(await host.Services.GetRequiredService<IRunRepository>().ListAsync());
            await repo.SetStatusAsync(binding.Id, binding.Version, actor, true);
            var accepted = await router.SubmitAsync(Message()); Assert.Equal("channel/telegram", accepted.Origin.Source);
            var done = await host.Services.GetRequiredService<TaskRunCoordinator>().WaitForTerminalAsync(accepted.Id).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(RunStatus.Completed, done.Status);
        }
        finally { await host.StopAsync(); }
    }

    [Fact]
    public async Task BindingMigrationPreservesExistingScheduleRowsAndStartsWithoutAnyGrant()
    {
        using var database = new TemporaryAgentDatabase(); var taskId = Guid.NewGuid(); var now = DateTimeOffset.UtcNow;
        ScheduledTask before;
        await using (var db = database.Open())
        {
            await db.GetService<IMigrator>().MigrateAsync("20261020075505_ScheduledDesktopPolicy");
            var task = new TaskDefinition { Id = taskId, Name = "Existing task", Instructions = "Legacy fixture", ModelProfileId = "fake", CreatedAtUtc = now, UpdatedAtUtc = now };
            before = await new SqliteScheduleRepository(database).SaveAsync(new(task, new() { Id = Guid.NewGuid(), TaskDefinitionId = taskId,
                Recurrence = new() { Mode = ScheduleMode.Interval, EveryMinutes = 10 }, TimeZoneId = "UTC", CreatedAtUtc = now, UpdatedAtUtc = now }));
            await db.Database.MigrateAsync();
        }
        await using var check = database.Open(); Assert.Equal(taskId, (await check.TaskDefinitions.SingleAsync()).Id); Assert.Empty(await check.ChannelBindings.ToArrayAsync());
        var restored = (await new SqliteScheduleRepository(database).GetAsync(before.Schedule.Id))!;
        Assert.Equal(before.Schedule.NextRunAtUtc, restored.Schedule.NextRunAtUtc); Assert.Equal(before.Schedule.DesktopPolicy, restored.Schedule.DesktopPolicy);
        Assert.False(await new ChannelOwnershipPolicy(Owner, new SqliteChannelBindingRepository(database)).AllowOutboundAsync(Output(Route), default));
    }

    private static IncomingMessage Message() => new() { Route = Route, SenderId = "owner-peer", ExternalMessageId = "message-1", Text = "Synthetic task", TimestampUtc = DateTimeOffset.UtcNow };
    private static OutgoingMessage Output(ChannelRoute route) => new() { Id = Guid.NewGuid(), Destination = route, Text = "Synthetic outgoing" };
    private static async Task Migrate(TemporaryAgentDatabase database) { await using var db = database.Open(); await db.Database.MigrateAsync(); }
}
