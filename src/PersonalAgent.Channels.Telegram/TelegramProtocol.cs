using System.Text.Json;

namespace PersonalAgent.Channels.Telegram;

internal sealed class TelegramProtocolException(string code) : Exception(code)
{
    public string Code { get; } = code;
}
internal static class TelegramProtocol
{
    internal static Uri Endpoint(string? token, string method)
    {
        if (token is null || token.Length is < 8 or > 256) throw new TelegramProtocolException("MissingOrInvalidCredential");
        var parts = token.Split(':');
        if (parts.Length != 2 || parts[0].Length == 0 || !parts[0].All(char.IsAsciiDigit)
            || parts[1].Length == 0 || !parts[1].All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-')) throw new TelegramProtocolException("MissingOrInvalidCredential");
        return new Uri("https://api.telegram.org/bot" + token + "/" + method);
    }
    internal static async Task<JsonDocument> ReadBoundedAsync(HttpContent content, CancellationToken ct)
    {
        const int maxBytes = 4 * 1024 * 1024;
        if (content.Headers.ContentLength > maxBytes) throw new TelegramProtocolException("ResponseTooLarge");
        await using var stream = await content.ReadAsStreamAsync(ct);
        using var buffer = new MemoryStream(); var chunk = new byte[8192];
        while (true)
        {
            var read = await stream.ReadAsync(chunk, ct); if (read == 0) break;
            if (buffer.Length + read > maxBytes) throw new TelegramProtocolException("ResponseTooLarge");
            buffer.Write(chunk, 0, read);
        }
        return JsonDocument.Parse(buffer.ToArray(), new JsonDocumentOptions { MaxDepth = 32 });
    }
}
