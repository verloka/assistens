using System.Text.Json;
using PersonalAgent.Core.Security;

namespace PersonalAgent.Channels.Telegram;

public sealed record TelegramConnectionResult(bool Connected, string Code, string? BotId = null, string? BotUsername = null);
public sealed class TelegramConnectionProbe(ISecretStore secrets, HttpMessageHandler? handler = null) : IDisposable
{
    private readonly HttpClient client = new(handler ?? new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false }) { Timeout = Timeout.InfiniteTimeSpan };
    public async Task<TelegramConnectionResult> TestAsync(string reference, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        try
        {
            var endpoint = TelegramProtocol.Endpoint(await secrets.ReadAsync(reference, ct), "getMe");
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct); deadline.CancelAfter(TimeSpan.FromSeconds(15));
            using var request = new HttpRequestMessage(HttpMethod.Post, endpoint);
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
            using var document = await TelegramProtocol.ReadBoundedAsync(response.Content, deadline.Token);
            var root = document.RootElement;
            if (!response.IsSuccessStatusCode || !root.TryGetProperty("ok", out var ok) || ok.ValueKind != JsonValueKind.True)
            {
                var code = root.TryGetProperty("error_code", out var errorCode) && errorCode.TryGetInt32(out var apiCode) ? apiCode : (int)response.StatusCode;
                return new(false, code is 401 or 403 ? "Unauthorized" : code == 429 ? "RateLimited" : "Rejected");
            }
            var result = root.GetProperty("result");
            if (!result.GetProperty("is_bot").GetBoolean() || !result.GetProperty("id").TryGetInt64(out var id) || id <= 0) return new(false, "InvalidResponse");
            var username = result.TryGetProperty("username", out var usernameValue) ? usernameValue.GetString() : null;
            if (username is not null && (username.Length is < 1 or > 64 || username.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '_'))) return new(false, "InvalidResponse");
            return new(true, "Connected", id.ToString(System.Globalization.CultureInfo.InvariantCulture), username);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (OperationCanceledException) { return new(false, "Timeout"); }
        catch (TelegramProtocolException error) { return new(false, error.Code); }
        catch (Exception error) when (error is HttpRequestException or IOException) { return new(false, "NetworkUnavailable"); }
        catch (Exception error) when (error is JsonException or InvalidOperationException or KeyNotFoundException or FormatException) { return new(false, "InvalidResponse"); }
    }
    public void Dispose() => client.Dispose();
}
