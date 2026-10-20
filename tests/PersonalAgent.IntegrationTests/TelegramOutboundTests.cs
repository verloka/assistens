using Microsoft.EntityFrameworkCore;
using PersonalAgent.Channels.Telegram;
using PersonalAgent.Core.Application;
using PersonalAgent.Core.Channels;
using PersonalAgent.Core.Domain;
using PersonalAgent.Core.Security;
using PersonalAgent.Persistence;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;

namespace PersonalAgent.IntegrationTests;

public sealed class TelegramOutboundTests
{
    private static readonly ChannelRoute Route = new("telegram", "fixture-bot", "-100000000001");
    private const string Token = "12345:synthetic-secret-never-log";

    [Fact]
    public async Task CompletedSummaryPreservesLiteralUnicodeAcrossChunksAndLeavesRunUnchanged()
    {
        await using var fixture = await HumanApprovalTests.Fixture.Start(true); var request = await fixture.Pending();
        await fixture.Approvals.DecideAsync(new(request.Id, request.Version, request.ActionHash, HumanApprovalTests.Fixture.Owner, true));
        await fixture.UntilFinished(); var run = (await fixture.Repository.GetAsync(fixture.RunId))!.Run;
        var longSummary = new string('a', 4095) + "😀 <b>literal</b> **literal** " + new string('я', 5000);
        var message = TelegramOutboundChannel.RunMessage(Guid.NewGuid(), Route, run with { Summary = longSummary });
        using var http = new Http(); using var channel = new TelegramOutboundChannel(Route.ConnectionId, "reference", new Secrets(), new Policy(), handler: http);
        await channel.SendAsync(message, default);
        Assert.Equal(message.Text, string.Concat(http.Bodies.Select(body => body.GetProperty("text").GetString())));
        Assert.True(http.Bodies.Count >= 3); Assert.All(http.Bodies, body =>
        {
            var text = body.GetProperty("text").GetString()!; Assert.InRange(text.Length, 1, 4096); Assert.False(char.IsHighSurrogate(text[^1])); Assert.False(char.IsLowSurrogate(text[0]));
            Assert.False(body.TryGetProperty("parse_mode", out _)); Assert.False(body.TryGetProperty("reply_markup", out _)); Assert.True(body.GetProperty("link_preview_options").GetProperty("is_disabled").GetBoolean());
        });
        Assert.Contains("<b>literal</b>", message.Text); Assert.Contains("**literal**", message.Text);
        Assert.Equal(JsonSerializer.Serialize(run), JsonSerializer.Serialize((await fixture.Repository.GetAsync(fixture.RunId))!.Run));
    }

    [Fact]
    public async Task PendingExactActionCreatesRandomScopedHashOnlyButtonsAndPresentationDoesNotApprove()
    {
        await using var fixture = await HumanApprovalTests.Fixture.Start(true); var request = await fixture.Pending();
        var bindings = new SqliteChannelBindingRepository(fixture.Database, fixture.Clock);
        var pending = await bindings.RequestPairingAsync(Route, "owner", HumanApprovalTests.Fixture.Owner);
        await bindings.SetStatusAsync(pending.Id, pending.Version, pending.Owner, true);
        var policy = new ChannelOwnershipPolicy(pending.Owner, bindings); var grants = new SqliteChannelActionRepository(fixture.Database);
        var presenter = new ChannelApprovalPresenter(policy, fixture.Approvals, grants, fixture.Clock);
        var peer = Peer(); var message = await presenter.CreateAsync(Guid.NewGuid(), peer, request.Id);
        Assert.Contains(request.Action.Destination, message.Text); Assert.Contains(request.Action.Arguments.GetRawText(), message.Text); Assert.Contains(request.ActionHash, message.Text);
        Assert.Equal(2, message.Actions.Count); Assert.Equal(2, message.Actions.Select(action => action.Token).Distinct().Count());
        foreach (var button in message.Actions)
        {
            Assert.Equal(43, button.Token.Length);
            var grant = (await new SqliteChannelActionRepository(fixture.Database).FindAsync(ChannelApprovalPresenter.HashToken(button.Token)))!;
            Assert.Equal(request.Id, grant.ApprovalId); Assert.Equal(request.ActionHash, grant.ActionHash); Assert.Equal(request.Version, grant.ApprovalVersion);
            Assert.Equal(peer.SenderId, grant.SenderId); Assert.Equal(Route, grant.Route); Assert.Equal(pending.Owner, grant.Owner); Assert.Equal(request.Action.RunId, grant.RunId);
            Assert.Equal(request.Action.RunVersion, grant.RunVersion); Assert.Equal(button.Kind, grant.Kind); Assert.True(grant.ExpiresAtUtc <= request.ExpiresAtUtc);
            await using var db = fixture.Database.Open(); Assert.DoesNotContain(button.Token, JsonSerializer.Serialize(await db.ChannelActions.ToArrayAsync()));
        }
        using var http = new Http(); using var channel = new TelegramOutboundChannel(Route.ConnectionId, "reference", new Secrets(), policy, presenter, http);
        await channel.SendAsync(message, default); var body = Assert.Single(http.Bodies);
        var rows = body.GetProperty("reply_markup").GetProperty("inline_keyboard").EnumerateArray().ToArray(); Assert.Equal(2, rows.Length);
        Assert.Equal(message.Actions[0].Token, rows[0][0].GetProperty("callback_data").GetString());
        Assert.Equal(ApprovalStatus.Pending, (await fixture.Approvals.GetAsync(request.Id))!.Status); Assert.Equal(0, fixture.Calls);
        foreach (var changed in new[] { message with { Id = Guid.NewGuid() }, message with { Text = "Approve a different harmless operation" },
            message with { Destination = Route with { DestinationId = "123" } }, message with { RunId = Guid.NewGuid() },
            message with { Actions = [message.Actions[0] with { Label = "Cancel" }] } })
            await Assert.ThrowsAsync<ChannelAccessDeniedException>(() => presenter.ValidateAsync(changed));
        var binding = (await bindings.FindAsync(Route, "owner"))!; await bindings.SetStatusAsync(binding.Id, binding.Version, binding.Owner, false);
        await Assert.ThrowsAsync<ChannelAccessDeniedException>(() => channel.SendAsync(message, default)); Assert.Single(http.Bodies);
        binding = (await bindings.FindAsync(Route, "owner"))!; await bindings.SetStatusAsync(binding.Id, binding.Version, binding.Owner, true);
        fixture.Clock.Now += TimeSpan.FromMinutes(2);
        await Assert.ThrowsAsync<ChannelAccessDeniedException>(() => channel.SendAsync(message, default)); Assert.Single(http.Bodies);
        Assert.Equal(0, fixture.Calls);
    }

    [Fact]
    public async Task LongExactReviewPlacesButtonsOnlyOnFinalChunkAndOversizedReviewRequiresLocalApp()
    {
        await using var fixture = await HumanApprovalTests.Fixture.Start(true); var original = await fixture.Pending();
        var request = await fixture.Approvals.CreateAsync(original.Action with { ToolCallId = "long-channel-review", Description = new string('x', 4096) }, original.Owner, TimeSpan.FromMinutes(1));
        var bindings = new SqliteChannelBindingRepository(fixture.Database, fixture.Clock);
        var pending = await bindings.RequestPairingAsync(Route, "owner", original.Owner); await bindings.SetStatusAsync(pending.Id, pending.Version, pending.Owner, true);
        var policy = new ChannelOwnershipPolicy(original.Owner, bindings); var grants = new SqliteChannelActionRepository(fixture.Database);
        var presenter = new ChannelApprovalPresenter(policy, fixture.Approvals, grants, fixture.Clock);
        var message = await presenter.CreateAsync(Guid.NewGuid(), Peer(), request.Id);
        using var http = new Http(); using var channel = new TelegramOutboundChannel(Route.ConnectionId, "reference", new Secrets(), policy, presenter, http);
        await channel.SendAsync(message, default); Assert.True(http.Bodies.Count > 1);
        Assert.All(http.Bodies.SkipLast(1), body => Assert.False(body.TryGetProperty("reply_markup", out _)));
        Assert.True(http.Bodies[^1].TryGetProperty("reply_markup", out _)); Assert.Equal(message.Text, string.Concat(http.Bodies.Select(body => body.GetProperty("text").GetString())));
        var huge = await fixture.Approvals.CreateAsync(original.Action with { ToolCallId = "oversized-channel-review", Description = new string('x', 4096),
            Arguments = JsonSerializer.SerializeToElement(new { target = new string('x', 62_000) }) }, original.Owner, TimeSpan.FromMinutes(1));
        var fallback = await presenter.CreateAsync(Guid.NewGuid(), Peer(), huge.Id);
        Assert.Empty(fallback.Actions); Assert.Contains("Open Personal Agent", fallback.Text);
        await using var db = fixture.Database.Open(); Assert.Equal(2, await db.ChannelActions.CountAsync()); Assert.Equal(0, fixture.Calls);
    }

    [Theory]
    [InlineData(429, false, true)]
    [InlineData(503, false, true)]
    [InlineData(401, false, false)]
    [InlineData(200, true, false)]
    public async Task KnownRejectionsAndUnknownResponsesAreSanitizedAndNeverRetried(int status, bool unknown, bool retryable)
    {
        using var http = new Http { Status = (HttpStatusCode)status, Response = status == 200 ? "unparseable " + Token : JsonSerializer.Serialize(new { ok = false, error_code = status, description = Token, parameters = new { retry_after = 120 } }) };
        using var channel = new TelegramOutboundChannel(Route.ConnectionId, "reference", new Secrets(), new Policy(), handler: http);
        var error = await Assert.ThrowsAsync<ChannelDeliveryException>(() => channel.SendAsync(Output("Synthetic summary"), default));
        Assert.Equal(unknown, error.OutcomeUnknown); Assert.Equal(retryable, error.Retryable); Assert.DoesNotContain(Token, error.ToString()); Assert.Single(http.Bodies);
        if (status == 429) Assert.Equal(TimeSpan.FromSeconds(120), error.RetryAfter);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NetworkErrorsDistinguishUndispatchedDnsFromUnknownWriteWithoutLeakingCredential(bool dns)
    {
        using var http = new Http { Failure = new HttpRequestException(dns ? HttpRequestError.NameResolutionError : HttpRequestError.ConnectionError, Token) };
        using var channel = new TelegramOutboundChannel(Route.ConnectionId, "reference", new Secrets(), new Policy(), handler: http);
        var error = await Assert.ThrowsAsync<ChannelDeliveryException>(() => channel.SendAsync(Output("Synthetic summary"), default));
        Assert.Equal(!dns, error.OutcomeUnknown); Assert.Equal(dns, error.Retryable); Assert.Single(http.Bodies); Assert.DoesNotContain(Token, error.ToString());
    }

    [Fact]
    public async Task PartialDeliveryIsNeverRetriedFromBeginningAndRevocationStopsLaterChunks()
    {
        var policy = new Policy(); using var http = new Http { AfterSend = () => policy.Allow = false };
        using var channel = new TelegramOutboundChannel(Route.ConnectionId, "reference", new Secrets(), policy, handler: http);
        var error = await Assert.ThrowsAsync<ChannelDeliveryException>(() => channel.SendAsync(Output(new string('a', 9000)), default));
        Assert.Equal(1, error.AcceptedParts); Assert.False(error.Retryable); Assert.Single(http.Bodies);
    }

    [Fact]
    public async Task CancellationAfterDispatchIsUnknownWhilePreCancelledAndUnauthorizedHaveNoNetworkEffect()
    {
        using var stop = new CancellationTokenSource(); using var http = new Http { Hold = true };
        using var channel = new TelegramOutboundChannel(Route.ConnectionId, "reference", new Secrets(), new Policy(), handler: http);
        var sending = channel.SendAsync(Output("Synthetic summary"), stop.Token); await http.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5)); stop.Cancel();
        var error = await Assert.ThrowsAsync<ChannelDeliveryException>(() => sending); Assert.True(error.OutcomeUnknown); Assert.False(error.Retryable);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => channel.SendAsync(Output("Synthetic summary"), stop.Token)); Assert.Single(http.Bodies);
        using var deniedHttp = new Http(); using var denied = new TelegramOutboundChannel(Route.ConnectionId, "reference", new Secrets(), new Policy { Allow = false }, handler: deniedHttp);
        await Assert.ThrowsAsync<ChannelAccessDeniedException>(() => denied.SendAsync(Output("Synthetic summary"), default)); Assert.Empty(deniedHttp.Bodies);
        await Assert.ThrowsAsync<ChannelAccessDeniedException>(() => channel.SendAsync(Output("Synthetic summary") with { Destination = Route with { ConnectionId = "other-bot" } }, default));
    }

    private static IncomingMessage Peer() => new() { Route = Route, SenderId = "owner", ExternalMessageId = "42", TimestampUtc = DateTimeOffset.UtcNow, Text = "Synthetic pending action" };
    private static OutgoingMessage Output(string text) => new() { Id = Guid.NewGuid(), Destination = Route, Text = text };
    private sealed class Secrets : ISecretStore
    {
        public ValueTask<string> StoreAsync(string secret, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask<string?> ReadAsync(string reference, CancellationToken cancellationToken = default) { Assert.Equal("reference", reference); return ValueTask.FromResult<string?>(Token); }
    }
    private sealed class Policy : IChannelAccessPolicy
    {
        public bool Allow = true;
        public ValueTask<bool> AllowInboundAsync(IncomingMessage message, CancellationToken ct) => ValueTask.FromResult(Allow);
        public ValueTask<bool> AllowOutboundAsync(OutgoingMessage message, CancellationToken ct) => ValueTask.FromResult(Allow && message.Destination == Route);
    }
    private sealed class Http : HttpMessageHandler
    {
        public List<JsonElement> Bodies = []; public HttpStatusCode Status = HttpStatusCode.OK; public string? Response; public Exception? Failure; public bool Hold; public Action? AfterSend;
        public TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Assert.Equal("api.telegram.org", request.RequestUri!.Host); Assert.EndsWith("/sendMessage", request.RequestUri.AbsolutePath); Assert.Equal(HttpMethod.Post, request.Method);
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct)); Bodies.Add(body.RootElement.Clone()); Entered.TrySetResult();
            if (Failure is not null) throw Failure; if (Hold) await Task.Delay(Timeout.InfiniteTimeSpan, ct); AfterSend?.Invoke();
            return new(Status) { Content = new StringContent(Response ?? JsonSerializer.Serialize(new { ok = true, result = new { message_id = Bodies.Count, chat = new { id = -100000000001L } } }), Encoding.UTF8, "application/json") };
        }
    }
}
