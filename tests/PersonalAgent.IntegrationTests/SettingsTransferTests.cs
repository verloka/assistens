using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using PersonalAgent.App;
using PersonalAgent.App.ViewModels;
using PersonalAgent.Core.Agents;
using PersonalAgent.Core.Application;
using PersonalAgent.Core.Channels;
using PersonalAgent.Core.Domain;
using PersonalAgent.Core.Persistence;
using PersonalAgent.Core.Security;
using PersonalAgent.IntegrationTests.Fakes;
using PersonalAgent.Persistence;
using System.Text.Json;

namespace PersonalAgent.IntegrationTests;

[Collection("Desktop UI")]
public sealed class SettingsTransferTests
{
    private const string Secret = "synthetic_secret_canary_120_XYZ";
    private static readonly ApprovalOwner Owner = new("desktop", "fixture-owner");
    private static ModelProfile Model(string reference) => new() { Id = "fixture", ProviderId = "openai", ModelId = "fixture-model", SecretRef = reference,
        Capabilities = new(true, true), GenerationSettings = new(MaxOutputTokens: 2048) };

    [Fact]
    public async Task ExportExcludesSecretsAndHistoryAndImportRetainsOnlyLocalKeysWithDisabledFreshIdentities()
    {
        using var source = new TemporaryAgentDatabase(); using var target = new TemporaryAgentDatabase();
        var sourceKey = "dpapi:" + Guid.NewGuid().ToString("N"); var targetKey = "dpapi:" + Guid.NewGuid().ToString("N");
        await Seed(source, sourceKey);
        var run = await HistoryRetentionTests.SeedAsync(target);
        await new SqliteModelProfileRepository(target).SaveAsync(Model(targetKey));
        var outbox = new SqliteNotificationOutbox(target);
        var evt = (await new SqliteRunRepository(target).GetAsync(run.Id))!.Events.Last();
        var message = new OutgoingMessage { Id = Guid.NewGuid(), RunId = run.Id, Text = Secret, Destination = new("fixture", "fixture", "peer") };
        await outbox.EnqueueAsync(evt.Id, message); var claim = (await outbox.ClaimNextAsync(DateTimeOffset.UtcNow))!;
        await outbox.FinishAsync(message.Id, claim.Version, NotificationDeliveryStatus.OutcomeUnknown, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, "DeliveryOutcomeUnknown");
        var originalRun = JsonSerializer.Serialize(await new SqliteRunRepository(target).GetAsync(run.Id));
        var originalDelivery = JsonSerializer.Serialize(await outbox.GetAsync(message.Id));
        var json = await new SettingsTransfer(source).ExportAsync(Owner);
        Assert.DoesNotContain(Secret, json); Assert.DoesNotContain("dpapi:", json); Assert.DoesNotContain("SecretRef", json);
        Assert.DoesNotContain("Approval", json); Assert.DoesNotContain("Instructions", json); Assert.DoesNotContain("SerializedState", json);
        Assert.DoesNotContain("synthetic-private-history-117", json); Assert.DoesNotContain("Permissions", json);
        var package = SettingsTransfer.Parse(json); Assert.Equal(45, package.RetentionDays);
        Assert.Equal("https://example.test", package.Mcp.Single(item => item.Transport == McpTransportKind.StreamableHttp).HttpOrigin);
        await new SettingsTransfer(target).ImportAsync(package, Owner);
        var model = Assert.Single(await new SqliteModelProfileRepository(target).ListAsync());
        Assert.Equal(targetKey, model.SecretRef); Assert.Equal(2048, model.GenerationSettings.MaxOutputTokens);
        var connections = await new SqliteMcpConnectionRepository(target).ListAsync(); Assert.Equal(2, connections.Count);
        Assert.All(connections, item => { Assert.False(item.Configuration.Enabled); Assert.False(item.Configuration.Approved); Assert.Empty(item.Permissions); Assert.Empty(item.Configuration.AllowedTools); });
        var stdio = connections.Single(item => item.Configuration.Stdio is not null).Configuration.Stdio!;
        Assert.Empty(stdio.Arguments); Assert.Null(stdio.SecretEnvironment);
        var schedule = Assert.Single(await new SqliteScheduleRepository(target).ListAsync());
        Assert.False(schedule.Task.Enabled); Assert.False(schedule.Schedule.Enabled); Assert.Null(schedule.Schedule.NextRunAtUtc);
        Assert.Equal(SettingsTransfer.ImportedInstructions, schedule.Task.Instructions);
        var channel = Assert.Single(await new SqliteChannelConfigurationRepository(target).ListAsync(Owner));
        Assert.False(channel.Enabled); Assert.False(channel.PeerAuthorized); Assert.False(channel.ReceiveTasks); Assert.Null(channel.SecretReference);
        Assert.Null(await new SqliteChannelBindingRepository(target).FindAsync(channel.Route, channel.SenderId));
        Assert.Equal(new(false, 45), await new SqliteHistoryRetentionRepository(target).ReadPolicyAsync());
        Assert.Equal(originalRun, JsonSerializer.Serialize(await new SqliteRunRepository(target).GetAsync(run.Id)));
        Assert.Equal(originalDelivery, JsonSerializer.Serialize(await outbox.GetAsync(message.Id)));
        await using var sourceDb = source.Open();
        Assert.DoesNotContain(channel.Id, await sourceDb.ChannelConfigurations.Select(row => row.Id).ToArrayAsync());
    }

    [Theory]
    [InlineData("version")][InlineData("unknown-field")][InlineData("duplicate-field")][InlineData("secret-ref")]
    [InlineData("duplicate-model")][InlineData("endpoint-secret")][InlineData("oversize")][InlineData("bad-recurrence")]
    public async Task UntrustedPackageIsRejectedBeforeAnyLocalConfigurationChanges(string failure)
    {
        using var source = new TemporaryAgentDatabase(); await Seed(source, "dpapi:" + Guid.NewGuid().ToString("N"));
        var json = await new SettingsTransfer(source).ExportAsync(Owner);
        var package = SettingsTransfer.Parse(json);
        json = failure switch
        {
            "version" => JsonSerializer.Serialize(package with { SchemaVersion = 999 }),
            "unknown-field" => json.Insert(1, "\"Enabled\":true,"),
            "secret-ref" => json.Insert(1, "\"SecretRef\":\"dpapi:00000000000000000000000000000000\","),
            "duplicate-field" => json.Insert(1, "\"SchemaVersion\":1,"),
            "duplicate-model" => JsonSerializer.Serialize(package with { Models = [package.Models[0], package.Models[0]] }),
            "endpoint-secret" => JsonSerializer.Serialize(package with { Mcp = [new("HTTP", McpTransportKind.StreamableHttp, null, "https://example.test/?token=" + Secret, 15, 30)] }),
            "oversize" => new string(' ', SettingsTransfer.MaximumBytes + 1),
            "bad-recurrence" => JsonSerializer.Serialize(package with { Schedules = [package.Schedules[0] with { Recurrence = new() { Mode = ScheduleMode.Interval, EveryMinutes = 0 } }] }),
            _ => json
        };
        Assert.ThrowsAny<Exception>(() => SettingsTransfer.Parse(json));
        Assert.Single(await new SqliteModelProfileRepository(source).ListAsync());
        Assert.Equal(2, (await new SqliteMcpConnectionRepository(source).ListAsync()).Count);
    }

    [Fact]
    public async Task MissingOrForeignLocalCredentialAndActualWriteFailureRollBackTheEntireImport()
    {
        using var source = new TemporaryAgentDatabase(); using var target = new TemporaryAgentDatabase();
        await Seed(source, "dpapi:" + Guid.NewGuid().ToString("N"));
        await using (var db = target.Open()) await db.Database.MigrateAsync();
        var transfer = new SettingsTransfer(target); var package = SettingsTransfer.Parse(await new SettingsTransfer(source).ExportAsync(Owner));
        await Assert.ThrowsAsync<System.IO.InvalidDataException>(() => transfer.ImportAsync(package, Owner));
        var local = Model("dpapi:" + Guid.NewGuid().ToString("N")) with { ProviderId = "anthropic" };
        var models = new SqliteModelProfileRepository(target); await models.SaveAsync(local);
        await Assert.ThrowsAsync<System.IO.InvalidDataException>(() => transfer.ImportAsync(package, Owner));
        await using (var db = target.Open()) await db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE ModelProfiles SET ProfileJson = {JsonSerializer.Serialize(local with { Id = "foreign-id", ProviderId = "openai" })} WHERE Id = {local.Id}");
        await Assert.ThrowsAsync<System.IO.InvalidDataException>(() => transfer.ImportAsync(package, Owner));
        local = local with { ProviderId = "openai", GenerationSettings = new(MaxOutputTokens: 512) }; await models.SaveAsync(local);
        await using (var db = target.Open()) await db.Database.ExecuteSqlRawAsync("CREATE TRIGGER FailSettings BEFORE INSERT ON Schedules BEGIN SELECT RAISE(ABORT, 'synthetic failure'); END;");
        await Assert.ThrowsAsync<DbUpdateException>(() => transfer.ImportAsync(package, Owner));
        Assert.Equal(local, Assert.Single(await models.ListAsync()));
        await using var final = target.Open(); Assert.Empty(await final.McpConnections.ToArrayAsync()); Assert.Empty(await final.Schedules.ToArrayAsync());
        Assert.Empty(await final.TaskDefinitions.ToArrayAsync()); Assert.Empty(await final.ChannelConfigurations.ToArrayAsync()); Assert.Empty(await final.HistoryRetention.ToArrayAsync());
    }

    [Fact]
    public Task ActualSettingsXamlRequiresPreviewAndSingleApplyAndImportedTelegramCanBindItsFirstLocalKeyOnly() => PrivacyUiTests.OnWpfThread(async () =>
    {
        using var source = new TemporaryAgentDatabase(); using var target = new TemporaryAgentDatabase();
        await Seed(source, "dpapi:" + Guid.NewGuid().ToString("N"));
        await using (var db = target.Open()) await db.Database.MigrateAsync();
        var local = Model("dpapi:" + Guid.NewGuid().ToString("N")); await new SqliteModelProfileRepository(target).SaveAsync(local);
        using var host = AppHost.CreateBuilder([], target.DatabasePath).ConfigureLogging(builder => builder.ClearProviders()).ConfigureServices(services =>
            services.AddSingleton<ISecretStore>(new WindowsDpapiSecretStore(System.IO.Path.Combine(System.IO.Path.GetDirectoryName(target.DatabasePath)!, "secrets")))).Build();
        await host.StartAsync(); var model = host.Services.GetRequiredService<SettingsViewModel>();
        var main = host.Services.GetRequiredService<MainWindow>(); var window = new SettingsWindow(model);
        try
        {
            window.Measure(new System.Windows.Size(700, 510)); window.Arrange(new System.Windows.Rect(0, 0, 700, 510)); window.UpdateLayout();
            Assert.False(window.ShowActivated); Assert.NotNull(main.FindName("SettingsButton")); Assert.False(model.CanApply);
            var json = await new SettingsTransfer(source).ExportAsync(Owner); model.Load(json); Assert.True(model.CanApply);
            Assert.DoesNotContain(Secret, model.Preview); model.FileFailure(); Assert.False(model.CanApply);
            model.Load(json); await model.ApplyAsync(); Assert.False(model.CanApply); Assert.Contains("Restart", model.Message);
            await model.ApplyAsync(); Assert.Equal(2, (await new SqliteMcpConnectionRepository(target).ListAsync()).Count);
            var channels = host.Services.GetRequiredService<ChannelsViewModel>(); await channels.Initialization;
            channels.RefreshCommand.Execute(null); await channels.RefreshCommand.ExecutionTask!;
            channels.Selected = Assert.Single(channels.Connections);
            Assert.Null(channels.Selected.SecretReference);
            channels.SetPendingSecret("12345:ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefgh"); channels.SaveCommand.Execute(null); await channels.SaveCommand.ExecutionTask!;
            Assert.StartsWith("dpapi:", channels.Selected!.SecretReference); Assert.False(channels.Selected.Enabled); Assert.Null(channels.Selected.RemoteIdentity);
            var saved = channels.Selected;
            await Assert.ThrowsAsync<ChannelConfigurationConflictException>(() => host.Services.GetRequiredService<IChannelConfigurationRepository>()
                .SaveAsync(saved with { SecretReference = "dpapi:" + Guid.NewGuid().ToString("N") }, saved.Version));
            await model.BackupAsync();
            Assert.Contains("Private database backup saved locally", model.Message);
            Assert.Single(System.IO.Directory.GetFiles(target.DatabasePath + ".backups", "*.db"));
            Assert.Empty(host.Services.GetRequiredService<TaskRunCoordinator>().GetRuns());
        }
        finally { window.Close(); main.Close(); await host.StopAsync(); }
    });

    private static async Task Seed(TemporaryAgentDatabase database, string reference)
    {
        await HistoryRetentionTests.SeedAsync(database);
        await new SqliteModelProfileRepository(database).SaveAsync(Model(reference));
        var mcp = new SqliteMcpConnectionRepository(database);
        await mcp.SaveAsync(new() { Configuration = new() { Id = Guid.NewGuid(), Name = "CLI", Transport = McpTransportKind.Stdio, Enabled = true, Approved = true,
            Stdio = new(System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe"), ["--token", Secret], SecretEnvironment: new Dictionary<string, string> { ["API_TOKEN"] = reference }) } });
        await mcp.SaveAsync(new() { Configuration = new() { Id = Guid.NewGuid(), Name = "HTTP", Transport = McpTransportKind.StreamableHttp, Enabled = true, Approved = true,
            Http = new(new("https://example.test/" + Secret + "?token=" + Secret), reference) } });
        var now = DateTimeOffset.UtcNow; var taskId = Guid.NewGuid();
        await new SqliteScheduleRepository(database).SaveAsync(new(new() { Id = taskId, Name = "Schedule", Instructions = Secret, ModelProfileId = "fixture", CreatedAtUtc = now, UpdatedAtUtc = now },
            new() { Id = Guid.NewGuid(), TaskDefinitionId = taskId, Recurrence = new() { Mode = ScheduleMode.Interval, EveryMinutes = 10 }, TimeZoneId = "UTC", CreatedAtUtc = now, UpdatedAtUtc = now }));
        var id = Guid.NewGuid();
        await new SqliteChannelConfigurationRepository(database).SaveAsync(new(id, Owner, "Telegram", new("telegram", id.ToString("N"), "123"), "42", true, true, true,
            reference, [NotificationEventKind.Completed], now, RemoteIdentity: "777"), 0);
        await new SqliteHistoryRetentionRepository(database).SavePolicyAsync(new(true, 45));
    }
}
