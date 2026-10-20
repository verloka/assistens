using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using PersonalAgent.App;
using PersonalAgent.App.ViewModels;
using PersonalAgent.Channels.Telegram;
using PersonalAgent.Core.Application;
using PersonalAgent.Core.Agents;
using PersonalAgent.Core.Domain;
using PersonalAgent.Core.Channels;
using PersonalAgent.Core.Persistence;
using PersonalAgent.Core.Security;
using PersonalAgent.IntegrationTests.Fakes;
using PersonalAgent.Persistence;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace PersonalAgent.IntegrationTests;

[Collection("Desktop UI")]
public sealed class ChannelConfigurationTests
{
    private const string Token = "12345:synthetic-channel-secret";
    private static readonly ApprovalOwner Owner = new("desktop", "channel-owner");

    [Fact]
    public Task RealXamlSavesDpapiOnlyReferenceTestsSavedBotAndRequiresExplicitPeerAndEnable() => OnWpfThread(async () =>
    {
        using var database = new TemporaryAgentDatabase(); await using (var db = database.Open()) await db.Database.MigrateAsync();
        var script = new Script(); using var host = Host(database, script);
        var model = host.Services.GetRequiredService<ChannelsViewModel>(); await model.Initialization;
        var window = new ChannelsWindow(model); await Bind(window);
        try
        {
            Assert.Empty(model.Connections); Assert.Equal(0, script.Requests);
            Assert.False(window.IsVisible); Assert.False(window.ShowActivated);
            Assert.Same(model.SaveCommand, ((Button)window.FindName("SaveChannel")).Command);
            model.Name = "Synthetic bot"; model.Destination = "24"; model.Sender = "42";
            ((PasswordBox)window.FindName("BotToken")).Password = Token; model.AuthorizePeer = true; model.Enabled = true;
            Assert.False(model.TestCommand.CanExecute(null)); await Execute(model.SaveCommand);
            var saved = Assert.Single(model.Connections); Assert.False(saved.Enabled); Assert.Null(saved.RemoteIdentity); Assert.Equal(0, script.Requests);
            Assert.Empty(((PasswordBox)window.FindName("BotToken")).Password); Assert.StartsWith("dpapi:", saved.SecretReference);
            Assert.Equal(Token, await host.Services.GetRequiredService<ISecretStore>().ReadAsync(saved.SecretReference!));
            Assert.DoesNotContain(Token, JsonSerializer.Serialize(saved)); Assert.DoesNotContain(Token, Encoding.UTF8.GetString(File.ReadAllBytes(database.DatabasePath)));
            foreach (var file in Directory.EnumerateFiles(Path.Combine(Path.GetDirectoryName(database.DatabasePath)!, "secrets")))
                Assert.DoesNotContain(Token, Encoding.UTF8.GetString(File.ReadAllBytes(file)));
            Assert.True(model.TestCommand.CanExecute(null)); await Execute(model.TestCommand);
            saved = Assert.Single(model.Connections); Assert.Equal("777", saved.RemoteIdentity); Assert.Equal("fixture_bot", saved.RemoteUsername); Assert.False(saved.Enabled); Assert.Equal(1, script.Probes);
            Assert.Equal(0, script.Polls); Assert.Equal(0, script.Sends);
            model.Enabled = true; await Execute(model.SaveCommand); saved = Assert.Single(model.Connections); Assert.True(saved.Enabled);
            var subscriptions = await host.Services.GetRequiredService<INotificationSubscriptionSource>().ListAsync(); Assert.Equal(saved.Route, Assert.Single(subscriptions).Route);
            var policy = new ChannelOwnershipPolicy(Owner, host.Services.GetRequiredService<IChannelBindingRepository>(),
                new ConfiguredChannelPolicy(host.Services.GetRequiredService<IChannelConfigurationRepository>(), Owner, saved.Id));
            var peer = Peer(saved); await Assert.ThrowsAsync<ChannelAccessDeniedException>(() => policy.RequireOwnerAsync(peer)); // Receive is separately off.
            model.ReceiveTasks = true; await Execute(model.SaveCommand); Assert.Equal(Owner, await policy.RequireOwnerAsync(peer));
            model.AuthorizePeer = false; await Execute(model.SaveCommand); Assert.False(model.Selected!.Enabled);
            await Assert.ThrowsAsync<ChannelAccessDeniedException>(() => policy.RequireOwnerAsync(peer));
            Assert.Empty(await host.Services.GetRequiredService<INotificationSubscriptionSource>().ListAsync());
            Assert.Empty(host.Services.GetRequiredService<TaskRunCoordinator>().GetRuns());
        }
        finally { window.Close(); }
    });

    [Fact]
    public Task DesktopConfigurationAndDirtyOrClosedEditorCannotTestOrRetainToken() => OnWpfThread(async () =>
    {
        using var database = new TemporaryAgentDatabase(); var script = new Script(); using var host = Host(database, script); await host.StartAsync();
        var model = host.Services.GetRequiredService<ChannelsViewModel>(); await model.Initialization; var window = new ChannelsWindow(model);
        model.Kind = "desktop"; model.Name = "Local notifications"; model.AuthorizePeer = true; model.Enabled = true; await Execute(model.SaveCommand);
        var saved = Assert.Single(model.Connections); Assert.Equal(new ChannelRoute("desktop", "local", Owner.UserId), saved.Route); Assert.Null(saved.SecretReference); Assert.True(saved.Enabled);
        await Execute(model.TestCommand); Assert.Equal(0, script.Requests); Assert.Contains("local tray", model.Message);
        model.Name = "Unsaved"; Assert.False(model.TestCommand.CanExecute(null));
        await Execute(model.NewCommand); ((PasswordBox)window.FindName("BotToken")).Password = Token; window.Close();
        var another = new ChannelsWindow(model); try { await Execute(model.SaveCommand); Assert.Contains("numeric", model.Message); Assert.Empty(((PasswordBox)another.FindName("BotToken")).Password); }
        finally { another.Close(); await host.StopAsync(); }
    });

    [Theory]
    [InlineData(401, "Unauthorized")]
    [InlineData(429, "RateLimited")]
    [InlineData(503, "Rejected")]
    [InlineData(200, "InvalidResponse")]
    [InlineData(0, "NetworkUnavailable")]
    public async Task ProbeBoundsRequestAndReturnsUsefulFixedCodesWithoutTokenOrProviderDescriptions(int status, string expected)
    {
        using var database = new TemporaryAgentDatabase(); var secrets = Secrets(database); var reference = await secrets.StoreAsync(Token);
        var script = new Script { ProbeStatus = status }; using var probe = new TelegramConnectionProbe(secrets, new Http(script));
        var result = await probe.TestAsync(reference); Assert.False(result.Connected); Assert.Equal(expected, result.Code);
        Assert.DoesNotContain(Token, JsonSerializer.Serialize(result)); Assert.Equal(1, script.Probes);
        Assert.Equal(0, script.Polls); Assert.Equal(0, script.Sends);
    }

    [Fact]
    public async Task MissingProtectedTokenAndCancelledTestNeverMakeHttpCall()
    {
        using var database = new TemporaryAgentDatabase(); var script = new Script(); using var probe = new TelegramConnectionProbe(Secrets(database), new Http(script));
        Assert.Equal("MissingOrInvalidCredential", (await probe.TestAsync("dpapi:" + Guid.NewGuid().ToString("N"))).Code);
        using var cancel = new CancellationTokenSource(); cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => probe.TestAsync("ignored", cancel.Token)); Assert.Equal(0, script.Requests);
    }

    [Fact]
    public Task StaleEditorCannotOverwriteNewerPeerSettingsAndRejectedSavedTestHasUsefulPrivateMessage() => OnWpfThread(async () =>
    {
        using var database = new TemporaryAgentDatabase(); await using (var db = database.Open()) await db.Database.MigrateAsync();
        var script = new Script { ProbeStatus = 401 }; using var host = Host(database, script);
        var repository = host.Services.GetRequiredService<IChannelConfigurationRepository>();
        var reference = await host.Services.GetRequiredService<ISecretStore>().StoreAsync(Token);
        var saved = await repository.SaveAsync(Profile() with { SecretReference = reference }, 0);
        var model = host.Services.GetRequiredService<ChannelsViewModel>(); await model.Initialization; model.Selected = model.Connections.Single();
        model.Name = "Stale local edit"; Assert.False(model.TestCommand.CanExecute(null)); Assert.Equal(0, script.Probes);
        await repository.SaveAsync(saved with { Name = "Newer owner review" }, saved.Version);
        await Execute(model.SaveCommand); Assert.Contains("Settings changed", model.Message); Assert.Equal("Newer owner review", (await repository.ListAsync(Owner)).Single().Name);
        await Execute(model.RefreshCommand); await Execute(model.TestCommand);
        Assert.Contains("token was rejected", model.Message); Assert.DoesNotContain(Token, model.Message); Assert.Null(model.Selected!.RemoteIdentity);
        Assert.False(model.Selected.Enabled); Assert.Equal(1, script.Probes);
    });

    [Fact]
    public async Task ConfigurationCasOwnerIdentityAndUniqueEnabledBotPreventRebindingAndDuplicatePollers()
    {
        using var database = new TemporaryAgentDatabase(); await using (var db = database.Open()) await db.Database.MigrateAsync();
        var repository = new SqliteChannelConfigurationRepository(database); var config = await repository.SaveAsync(Profile(), 0);
        var attempts = await Task.WhenAll(Enumerable.Range(0, 8).Select(async index =>
        { try { return await repository.SaveAsync(config with { Name = "review " + index }, config.Version); } catch (ChannelConfigurationConflictException) { return null; } }));
        Assert.Single(attempts, value => value is not null); config = Assert.Single(await repository.ListAsync(Owner));
        foreach (var changed in new[] { config with { Owner = Owner with { UserId = "foreign" } }, config with { SenderId = "43" },
            config with { Route = config.Route with { DestinationId = "25" } }, config with { SecretReference = "dpapi:" + Guid.NewGuid().ToString("N") } })
            await Assert.ThrowsAsync<ChannelConfigurationConflictException>(() => repository.SaveAsync(changed, config.Version));
        Assert.Empty(await repository.ListAsync(Owner with { UserId = "foreign" }));
        var enabled = await repository.SaveAsync(config with { Enabled = true, RemoteIdentity = "777" }, config.Version);
        await Assert.ThrowsAsync<ChannelConfigurationConflictException>(() => repository.SaveAsync(Profile() with { Enabled = true, RemoteIdentity = "777" }, 0));
        await using var final = database.Open(); await final.ChannelConfigurations.Where(row => row.Id == enabled.Id).ExecuteUpdateAsync(set => set.SetProperty(row => row.PayloadSchemaVersion, 99));
        await Assert.ThrowsAsync<NotSupportedException>(() => repository.ListAsync(Owner));
    }

    [Fact]
    public async Task AdditiveConfigurationMigrationKeepsExistingRunBindingAndPendingOutbox()
    {
        using var database = new TemporaryAgentDatabase(); await using (var old = database.Open()) await old.GetService<IMigrator>().MigrateAsync("20261020094956_NotificationDeliveryOutbox");
        var repository = new SqliteRunRepository(database);
        var run = new TaskRun { Id = Guid.NewGuid(), ConversationId = Guid.NewGuid(), Instructions = "Migration source", ModelProfileId = "fake", Origin = new("desktop"), CreatedAtUtc = DateTimeOffset.UtcNow };
        var request = JsonSerializer.Serialize(new AgentRequest { RunId = run.Id, ConversationId = run.ConversationId, Instructions = run.Instructions, ModelProfileId = run.ModelProfileId });
        await using (var old = database.Open())
        {
            // Seed the historical schema directly; current run admission also writes newer message columns.
            await old.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO Conversations (Id, Title, CreatedAtUtc, UpdatedAtUtc) VALUES ({run.ConversationId}, {run.Instructions}, {run.CreatedAtUtc.UtcTicks}, {run.CreatedAtUtc.UtcTicks})");
            await old.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO TaskRuns (Id, ConversationId, OriginJson, Instructions, ModelProfileId, Status, RunVersion, CreatedAtUtc, SourcesJson, RequestJson, PayloadSchemaVersion) VALUES ({run.Id}, {run.ConversationId}, {JsonSerializer.Serialize(run.Origin)}, {run.Instructions}, {run.ModelProfileId}, {"Created"}, {0L}, {run.CreatedAtUtc.UtcTicks}, {"[]"}, {request}, {1})");
        }
        var profile = Profile(); var bindings = new SqliteChannelBindingRepository(database); var binding = await bindings.RequestPairingAsync(profile.Route, profile.SenderId, Owner);
        var message = new OutgoingMessage { Id = Guid.NewGuid(), Destination = profile.Route, Text = "Pending neutral notification", RunId = run.Id };
        var outbox = new SqliteNotificationOutbox(database); var delivery = await outbox.EnqueueAsync(Guid.NewGuid(), message);
        var before = JsonSerializer.Serialize(await repository.GetAsync(run.Id));
        await using (var current = database.Open()) await current.Database.MigrateAsync();
        Assert.Equal(before, JsonSerializer.Serialize(await repository.GetAsync(run.Id)));
        Assert.Equal(binding, await bindings.FindAsync(profile.Route, profile.SenderId));
        Assert.Equal(JsonSerializer.Serialize(delivery), JsonSerializer.Serialize(await outbox.GetAsync(message.Id)));
        Assert.Empty(await new SqliteChannelConfigurationRepository(database).ListAsync(Owner));
    }

    [Fact]
    public async Task ConfiguredHostedPollingRoutesOneTaskAndDurableRepliesAndDisableClosesAdmission()
    {
        using var database = new TemporaryAgentDatabase(); var script = new Script { Receive = true }; using var host = Host(database, script); await host.StartAsync();
        var repository = host.Services.GetRequiredService<IChannelConfigurationRepository>();
        var reference = await host.Services.GetRequiredService<ISecretStore>().StoreAsync(Token);
        var saved = await repository.SaveAsync(Profile() with { SecretReference = reference, ReceiveTasks = true, RemoteIdentity = "777", RemoteUsername = "fixture_bot" }, 0);
        var bindings = host.Services.GetRequiredService<IChannelBindingRepository>(); var pending = await bindings.RequestPairingAsync(saved.Route, saved.SenderId, Owner);
        await bindings.SetStatusAsync(pending.Id, pending.Version, Owner, true);
        saved = await repository.SaveAsync(saved with { Enabled = true }, saved.Version);
        await Until(async () => { await using var db = database.Open(); return await db.NotificationOutbox.CountAsync(row => row.Status == NotificationDeliveryStatus.Delivered) == 2; });
        var run = Assert.Single(await host.Services.GetRequiredService<IRunRepository>().ListAsync()); Assert.Equal("Synthetic configured task", run.Instructions);
        Assert.Equal(Core.Domain.RunStatus.Completed, run.Status); Assert.Equal(2, script.Sends); Assert.True(script.Polls >= 2);
        Assert.Equal(3, (await host.Services.GetRequiredService<IChannelCursorRepository>().ReadAsync("telegram", saved.Route.ConnectionId)).NextSequence);
        var cachedSender = (await host.Services.GetRequiredService<IOutboundChannelCatalog>().FindAsync(saved.Route))!;
        saved = await repository.SaveAsync(saved with { Enabled = false }, saved.Version);
        var policy = new ChannelOwnershipPolicy(Owner, bindings, new ConfiguredChannelPolicy(repository, Owner, saved.Id));
        await Assert.ThrowsAsync<ChannelAccessDeniedException>(() => policy.RequireOwnerAsync(Peer(saved)));
        Assert.Null(await host.Services.GetRequiredService<IOutboundChannelCatalog>().FindAsync(saved.Route));
        await Assert.ThrowsAsync<ChannelAccessDeniedException>(() => cachedSender.SendAsync(new OutgoingMessage { Id = Guid.NewGuid(), Destination = saved.Route, Text = "Never send after disable" }, default));
        Assert.Equal(2, script.Sends);
        await script.PollCancelled.Task.WaitAsync(TimeSpan.FromSeconds(5)); var count = script.Requests;
        await host.StopAsync(); Assert.Equal(count, script.Requests); Assert.Single(await host.Services.GetRequiredService<IRunRepository>().ListAsync());
    }

    private static IncomingMessage Peer(ChannelConfiguration profile) => new() { Route = profile.Route, SenderId = profile.SenderId, Text = "/status", TimestampUtc = DateTimeOffset.UtcNow, ExternalMessageId = "1" };
    private static ChannelConfiguration Profile()
    { var id = Guid.NewGuid(); return new(id, Owner, "Synthetic profile", new("telegram", id.ToString("N"), "24"), "42", false, false, true,
        "dpapi:" + Guid.NewGuid().ToString("N"), [NotificationEventKind.Completed], DateTimeOffset.UtcNow.AddMinutes(-1)); }
    private static WindowsDpapiSecretStore Secrets(TemporaryAgentDatabase database) => new(Path.Combine(Path.GetDirectoryName(database.DatabasePath)!, "secrets"));
    private static IHost Host(TemporaryAgentDatabase database, Script script) => AppHost.CreateBuilder([], database.DatabasePath).ConfigureLogging(log => log.ClearProviders()).ConfigureServices(services =>
    { services.AddSingleton(Owner); services.AddSingleton<ISecretStore>(Secrets(database)); services.AddSingleton<Func<HttpMessageHandler>>(() => new Http(script)); }).Build();
    private sealed class Script
    {
        public int ProbeStatus = -1, Probes, Polls, Sends; public bool Receive;
        public int Requests => Probes + Polls + Sends;
        public TaskCompletionSource PollCancelled = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
    private sealed class Http(Script script) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Assert.Equal("api.telegram.org", request.RequestUri!.Host); Assert.Equal(HttpMethod.Post, request.Method);
            if (request.RequestUri.AbsolutePath.EndsWith("/getMe"))
            {
                Interlocked.Increment(ref script.Probes); Assert.Null(request.Content);
                if (script.ProbeStatus == 0) throw new HttpRequestException(Token);
                if (script.ProbeStatus == 200) return Response("not-json");
                if (script.ProbeStatus > 0) return Response(JsonSerializer.Serialize(new { ok = false, error_code = script.ProbeStatus, description = Token }), (HttpStatusCode)script.ProbeStatus);
                return Response("{\"ok\":true,\"result\":{\"id\":777,\"is_bot\":true,\"username\":\"fixture_bot\"}}");
            }
            if (request.RequestUri.AbsolutePath.EndsWith("/sendMessage"))
            {
                Interlocked.Increment(ref script.Sends); using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct)); Assert.Equal(24, body.RootElement.GetProperty("chat_id").GetInt64());
                Assert.DoesNotContain(Token, body.RootElement.GetProperty("text").GetString());
                return Response(JsonSerializer.Serialize(new { ok = true, result = new { message_id = 10_000 + script.Sends, chat = new { id = 24 } } }));
            }
            Assert.EndsWith("/getUpdates", request.RequestUri.AbsolutePath); var poll = Interlocked.Increment(ref script.Polls); Assert.True(script.Receive);
            if (poll > 1)
            {
                try { await Task.Delay(Timeout.InfiniteTimeSpan, ct); } catch (OperationCanceledException) { script.PollCancelled.TrySetResult(); throw; }
            }
            var updates = new[] { Update(1, 999), Update(2, 42), Update(2, 42) }; return Response("{\"ok\":true,\"result\":[" + string.Join(',', updates) + "]}");
        }
        private static string Update(int id, int sender) => JsonSerializer.Serialize(new { update_id = id, message = new { message_id = id, date = 1800000000,
            from = new { id = sender, is_bot = false }, chat = new { id = 24 }, text = "/new@fixture_bot Synthetic configured task" } });
        private static HttpResponseMessage Response(string json, HttpStatusCode status = HttpStatusCode.OK) => new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
    }
    private static async Task Execute(AsyncCommand command) { Assert.True(command.CanExecute(null)); command.Execute(null); await command.ExecutionTask!.WaitAsync(TimeSpan.FromSeconds(10)); }
    private static async Task Until(Func<Task<bool>> condition) { using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15)); while (!await condition()) await Task.Delay(20, timeout.Token); }
    private static async Task Bind(Window window) { window.Measure(new Size(860, 710)); window.Arrange(new Rect(0, 0, 860, 710)); window.UpdateLayout(); await Dispatcher.Yield(DispatcherPriority.ApplicationIdle); }
    private static Task OnWpfThread(Func<Task> test)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher; SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            dispatcher.BeginInvoke(new Action(async () => { try { await test(); completion.TrySetResult(); } catch (Exception error) { completion.TrySetException(error); } finally { dispatcher.BeginInvokeShutdown(DispatcherPriority.Background); } })); Dispatcher.Run();
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); return completion.Task.WaitAsync(TimeSpan.FromSeconds(35));
    }
}
