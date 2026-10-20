using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;
using PersonalAgent.Core.Channels;
using PersonalAgent.Core.Persistence;
using PersonalAgent.Core.Security;

namespace PersonalAgent.Channels.Telegram;

public sealed record TelegramPollingOptions(string ConnectionId, string TokenReference, int PollTimeoutSeconds = 30, int MaxPollAttempts = 3);

// Deliberately excludes provider descriptions, request URIs and inner HTTP exceptions (the URI contains the bot token).
public sealed class TelegramPollingException(string code) : Exception("Telegram polling stopped: " + code)
{
    public string Code { get; } = code;
}

/// <summary>Explicitly started inbound adapter; does not create a webhook or enable any model tools.</summary>
public sealed class TelegramPollingReceiver : IInboundChannel, IDisposable
{
    private readonly TelegramPollingOptions options;
    private readonly ISecretStore secrets;
    private readonly IChannelCursorRepository cursors;
    private readonly HttpClient client;
    private readonly TimeProvider clock;
    private int running;
    public string ChannelId => "telegram";
    public string ConnectionId => options.ConnectionId;

    public TelegramPollingReceiver(TelegramPollingOptions options, ISecretStore secrets, IChannelCursorRepository cursors,
        HttpMessageHandler? handler = null, TimeProvider? timeProvider = null)
    {
        new ChannelRoute("telegram", options.ConnectionId, "configuration").Validate();
        if (string.IsNullOrWhiteSpace(options.TokenReference) || options.PollTimeoutSeconds is < 1 or > 50 || options.MaxPollAttempts is < 1 or > 5)
            throw new ArgumentException("Invalid Telegram polling configuration.");
        this.options = options; this.secrets = secrets; this.cursors = cursors; clock = timeProvider ?? TimeProvider.System;
        client = new HttpClient(handler ?? new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false }) { Timeout = Timeout.InfiniteTimeSpan };
    }

    public async Task RunAsync(Func<IncomingMessage, CancellationToken, Task> accept, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(accept);
        if (Interlocked.Exchange(ref running, 1) != 0) throw new InvalidOperationException("Telegram receiver is already running.");
        try
        {
            ct.ThrowIfCancellationRequested();
            var token = await secrets.ReadAsync(options.TokenReference, ct);
            if (!ValidToken(token)) throw new TelegramPollingException("MissingOrInvalidCredential");
            var endpoint = new Uri("https://api.telegram.org/bot" + token + "/getUpdates");
            var cursor = await cursors.ReadAsync(ChannelId, ConnectionId, ct);
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                using var response = await PollAsync(endpoint, cursor.NextSequence, ct);
                var updates = response.RootElement.GetProperty("result");
                if (updates.ValueKind != JsonValueKind.Array || updates.GetArrayLength() > 100) throw new TelegramPollingException("InvalidResponse");
                var ordered = updates.EnumerateArray().Select(update => (Id: UpdateId(update), Update: update)).OrderBy(item => item.Id).ToArray();
                foreach (var item in ordered)
                {
                    if (item.Id < cursor.NextSequence) continue;
                    var message = Map(item.Update, item.Id);
                    if (message is not null)
                    {
                        try { await accept(message, ct); }
                        catch (ChannelAccessDeniedException) { /* Explicit policy denial is a terminal discard, never a task. */ }
                    }
                    ct.ThrowIfCancellationRequested();
                    // https://core.telegram.org/bots/api#getupdates: higher offset confirms earlier updates.
                    // The next getUpdates acknowledges only positions durably committed after application acceptance.
                    // A crash here can redeliver: common external-update submission identity prevents another task/effect.
                    cursor = await cursors.AdvanceAsync(ChannelId, ConnectionId, cursor, checked(item.Id + 1), ct);
                }
            }
        }
        finally { Volatile.Write(ref running, 0); }
    }

    private async Task<JsonDocument> PollAsync(Uri endpoint, long offset, CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
        {
            var delay = TimeSpan.FromSeconds(attempt);
            var error = "TransportUnavailable";
            try
            {
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
                deadline.CancelAfter(TimeSpan.FromSeconds(options.PollTimeoutSeconds + 10));
                using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
                { Content = JsonContent.Create(new { offset, timeout = options.PollTimeoutSeconds, limit = 100, allowed_updates = new[] { "message", "callback_query" } }) };
                using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
                if ((int)response.StatusCode >= 500) error = "ServiceUnavailable";
                else
                {
                    var document = await ReadBoundedAsync(response.Content, deadline.Token);
                    var keep = false;
                    try
                    {
                        var root = document.RootElement;
                        if (response.IsSuccessStatusCode && root.TryGetProperty("ok", out var ok) && ok.ValueKind == JsonValueKind.True && root.TryGetProperty("result", out _))
                        { keep = true; return document; }
                        var code = root.TryGetProperty("error_code", out var value) && value.TryGetInt32(out var number) ? number : (int)response.StatusCode;
                        if (code != 429 && code < 500) throw new TelegramPollingException(code switch { 401 or 403 => "Unauthorized", 409 => "PollingConflict", _ => "Rejected" });
                        error = code == 429 ? "RateLimited" : "ServiceUnavailable";
                        if (root.TryGetProperty("parameters", out var parameters) && parameters.TryGetProperty("retry_after", out var retry)
                            && retry.TryGetInt32(out var seconds))
                        {
                            if (seconds is < 0 or > 60) throw new TelegramPollingException("RateLimited");
                            delay = TimeSpan.FromSeconds(Math.Max(seconds, attempt));
                        }
                    }
                    finally { if (!keep) document.Dispose(); }
                }
            }
            catch (HttpRequestException) { /* Never expose token-bearing request diagnostics. */ }
            catch (IOException) { }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { error = "PollTimeout"; }
            catch (OperationCanceledException) { throw new OperationCanceledException(ct); }
            catch (JsonException) { throw new TelegramPollingException("InvalidResponse"); }
            catch (InvalidOperationException) { throw new TelegramPollingException("InvalidResponse"); }
            if (attempt >= options.MaxPollAttempts) throw new TelegramPollingException(error);
            await Task.Delay(delay, clock, ct);
        }
    }

    private static async Task<JsonDocument> ReadBoundedAsync(HttpContent content, CancellationToken ct)
    {
        const int maxBytes = 4 * 1024 * 1024;
        if (content.Headers.ContentLength > maxBytes) throw new TelegramPollingException("ResponseTooLarge");
        await using var stream = await content.ReadAsStreamAsync(ct);
        using var buffer = new MemoryStream(); var chunk = new byte[8192];
        while (true)
        {
            var read = await stream.ReadAsync(chunk, ct); if (read == 0) break;
            if (buffer.Length + read > maxBytes) throw new TelegramPollingException("ResponseTooLarge");
            buffer.Write(chunk, 0, read);
        }
        return JsonDocument.Parse(buffer.ToArray(), new JsonDocumentOptions { MaxDepth = 32 });
    }

    private static long UpdateId(JsonElement update)
    {
        if (update.ValueKind != JsonValueKind.Object || !update.TryGetProperty("update_id", out var id) || id.ValueKind != JsonValueKind.Number || !id.TryGetInt64(out var number) || number < 0 || number == long.MaxValue)
            throw new TelegramPollingException("InvalidUpdateId");
        return number;
    }

    private IncomingMessage? Map(JsonElement update, long updateId)
    {
        try
        {
            var callback = update.TryGetProperty("callback_query", out var query);
            if (!callback && !update.TryGetProperty("message", out _)) return null; // Older unwanted kinds can still be delivered by Telegram.
            var item = callback ? query : update.GetProperty("message");
            if (!item.TryGetProperty("from", out var sender) || sender.GetProperty("is_bot").GetBoolean()) return null;
            var body = callback ? item.GetProperty("message") : item;
            if (!callback && !body.TryGetProperty("text", out _)) return null; // No attachment download or task inferred from captions/service messages.
            if (body.TryGetProperty("sender_chat", out _)) return null;
            return new IncomingMessage
            {
                Route = new(ChannelId, ConnectionId, body.GetProperty("chat").GetProperty("id").GetInt64().ToString(CultureInfo.InvariantCulture)),
                SenderId = sender.GetProperty("id").GetInt64().ToString(CultureInfo.InvariantCulture),
                ExternalMessageId = updateId.ToString(CultureInfo.InvariantCulture),
                TimestampUtc = DateTimeOffset.FromUnixTimeSeconds(body.GetProperty("date").GetInt64()),
                Text = callback ? "" : body.GetProperty("text").GetString()!,
                ActionToken = callback ? item.GetProperty("data").GetString() : null,
                CorrelationKey = callback ? item.GetProperty("id").GetString() : body.GetProperty("message_id").GetInt64().ToString(CultureInfo.InvariantCulture)
            }.Snapshot();
        }
        catch (Exception ex) when (ex is KeyNotFoundException or InvalidOperationException or FormatException or ArgumentException)
        { return null; } // Unsupported/malformed transport messages never acquire application authority.
    }

    private static bool ValidToken(string? token)
    {
        if (token is null || token.Length is < 8 or > 256) return false;
        var parts = token.Split(':');
        return parts.Length == 2 && parts[0].Length > 0 && parts[0].All(char.IsAsciiDigit)
            && parts[1].Length > 0 && parts[1].All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-');
    }

    public void Dispose() => client.Dispose();
}
