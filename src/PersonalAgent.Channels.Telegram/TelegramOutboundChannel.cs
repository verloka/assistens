using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using PersonalAgent.Core.Application;
using PersonalAgent.Core.Channels;
using PersonalAgent.Core.Domain;
using PersonalAgent.Core.Security;

namespace PersonalAgent.Channels.Telegram;

public sealed class TelegramOutboundChannel : IOutboundChannel, IDisposable
{
    private readonly string tokenReference;
    private readonly ISecretStore secrets;
    private readonly IChannelAccessPolicy policy;
    private readonly ChannelApprovalPresenter? approvals;
    private readonly HttpClient client;
    private readonly IChannelDeliveryReceiptSink? receipts;
    public string ChannelId => "telegram";
    public string ConnectionId { get; }
    public ChannelCapabilities Capabilities { get; } = new(true);
    public TelegramOutboundChannel(string connectionId, string tokenReference, ISecretStore secrets, IChannelAccessPolicy policy,
        ChannelApprovalPresenter? approvals = null, HttpMessageHandler? handler = null, IChannelDeliveryReceiptSink? receipts = null)
    {
        new ChannelRoute("telegram", connectionId, "configuration").Validate();
        ArgumentException.ThrowIfNullOrWhiteSpace(tokenReference);
        ConnectionId = connectionId; this.tokenReference = tokenReference; this.secrets = secrets; this.policy = policy; this.approvals = approvals;
        this.receipts = receipts;
        client = new HttpClient(handler ?? new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false }) { Timeout = Timeout.InfiniteTimeSpan };
    }
    public async Task SendAsync(OutgoingMessage incoming, CancellationToken ct)
    {
        var message = incoming.Snapshot(); ct.ThrowIfCancellationRequested();
        if (message.Destination.ChannelId != ChannelId || message.Destination.ConnectionId != ConnectionId
            || !long.TryParse(message.Destination.DestinationId, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var chatId) || chatId == 0)
            throw new ChannelAccessDeniedException(); // Numeric reviewed chat IDs only, no usernames/dynamic recipients.
        if (!await policy.AllowOutboundAsync(message, ct).ConfigureAwait(false)) throw new ChannelAccessDeniedException();
        if (message.Actions.Any(action => Encoding.UTF8.GetByteCount(action.Token) is < 1 or > 64)) throw new ArgumentException("Telegram action identifier exceeds its byte limit.");
        if (message.Actions.Count > 0)
        {
            if (approvals is null) throw new ChannelAccessDeniedException();
            await approvals.ValidateAsync(message, ct).ConfigureAwait(false);
        }
        Uri endpoint;
        try { endpoint = TelegramProtocol.Endpoint(await secrets.ReadAsync(tokenReference, ct).ConfigureAwait(false), "sendMessage"); }
        catch (TelegramProtocolException ex) { throw new ChannelDeliveryException(ex.Code, false); }
        var chunks = Split(message.Text); var accepted = 0;
        foreach (var text in chunks)
        {
            ct.ThrowIfCancellationRequested();
            if (!await policy.AllowOutboundAsync(message, ct).ConfigureAwait(false)) throw new ChannelDeliveryException("AuthorizationChanged", false, acceptedParts: accepted);
            var final = accepted == chunks.Count - 1;
            if (final && message.Actions.Count > 0) await approvals!.ValidateAsync(message, ct).ConfigureAwait(false);
            var buttons = final && message.Actions.Count > 0 ? new { inline_keyboard = message.Actions.Select(action => new[] { new { text = action.Label, callback_data = action.Token } }).ToArray() } : null;
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct); deadline.CancelAfter(TimeSpan.FromSeconds(30));
            var body = JsonSerializer.Serialize(new { chat_id = chatId, text, link_preview_options = new { is_disabled = true }, reply_markup = buttons },
                new JsonSerializerOptions { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull });
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, endpoint) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
                using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token).ConfigureAwait(false);
                using var document = await TelegramProtocol.ReadBoundedAsync(response.Content, deadline.Token).ConfigureAwait(false);
                var root = document.RootElement;
                if (root.TryGetProperty("ok", out var ok) && ok.ValueKind == JsonValueKind.False)
                {
                    var code = root.TryGetProperty("error_code", out var number) && number.TryGetInt32(out var value) ? value : (int)response.StatusCode;
                    TimeSpan? retry = null;
                    if (root.TryGetProperty("parameters", out var parameters) && parameters.TryGetProperty("retry_after", out var seconds) && seconds.TryGetInt32(out var delay) && delay >= 0)
                        retry = TimeSpan.FromSeconds(delay);
                    throw new ChannelDeliveryException(code == 429 ? "RateLimited" : "Rejected", false, code == 429 || code >= 500, accepted, retry);
                }
                if (!response.IsSuccessStatusCode || ok.ValueKind != JsonValueKind.True || !root.TryGetProperty("result", out var result)
                    || result.GetProperty("message_id").GetInt64() <= 0 || result.GetProperty("chat").GetProperty("id").GetInt64() != chatId)
                    throw new ChannelDeliveryException("UnconfirmedResponse", true, acceptedParts: accepted);
                accepted++;
                if (receipts is not null)
                {
                    try { await receipts.RecordAsync(message, result.GetProperty("message_id").GetInt64().ToString(CultureInfo.InvariantCulture), CancellationToken.None).ConfigureAwait(false); }
                    catch (Exception) { throw new ChannelDeliveryException("ReceiptNotStored", true, acceptedParts: accepted); }
                }
            }
            catch (HttpRequestException ex) { throw new ChannelDeliveryException("TransportUnavailable", ex.HttpRequestError != HttpRequestError.NameResolutionError, ex.HttpRequestError == HttpRequestError.NameResolutionError, accepted); }
            catch (Exception ex) when (ex is IOException or JsonException or TelegramProtocolException or InvalidOperationException or KeyNotFoundException or FormatException)
            { throw new ChannelDeliveryException("UnconfirmedResponse", true, acceptedParts: accepted); }
            catch (OperationCanceledException) { throw new ChannelDeliveryException("SendInterrupted", true, acceptedParts: accepted); }
        }
    }
    // Telegram sendMessage: 4096 characters. UTF-16 bound is conservative and never splits a surrogate pair.
    public static IReadOnlyList<string> Split(string text)
    {
        var chunks = new List<string>();
        for (var start = 0; start < text.Length;)
        {
            var length = Math.Min(4096, text.Length - start);
            if (start + length < text.Length && char.IsHighSurrogate(text[start + length - 1]) && char.IsLowSurrogate(text[start + length])) length--;
            chunks.Add(text.Substring(start, length)); start += length;
        }
        return chunks;
    }
    public static OutgoingMessage RunMessage(Guid id, ChannelRoute destination, TaskRun run) => new OutgoingMessage
    {
        Id = id, Destination = destination, RunId = run.Id, ConversationId = run.ConversationId,
        Text = Bound($"Run {run.Id:D}: {run.Status}\n" + (run.Summary ?? run.Reason ?? "") + (run.ErrorCode is { } error ? "\nError: " + error : ""))
    }.Snapshot();
    private static string Bound(string text)
    {
        if (text.Length <= 65_536) return text;
        var length = 65_400;
        if (char.IsHighSurrogate(text[length - 1]) && char.IsLowSurrogate(text[length])) length--;
        return text[..length] + "\n[Truncated; open Personal Agent for the complete result.]";
    }
    public void Dispose() => client.Dispose();
}
