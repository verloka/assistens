using Anthropic;
using Anthropic.Core;
using Anthropic.Exceptions;
using Microsoft.Extensions.AI;
using PersonalAgent.Core.Agents;
using PersonalAgent.Core.Security;
using System.Text.RegularExpressions;

namespace PersonalAgent.Agent;

// Only trusted composition/tests can supply a transport. Profiles cannot redirect credentials.
public sealed partial class AnthropicModelProvider(ISecretStore secrets, HttpClient? httpClient = null) : IModelProvider
{
    public string ProviderId => "anthropic";

    public async Task<ModelConnection> OpenAsync(ModelProfile profile, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Validate(profile);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        AnthropicClient? sdk = null;
        try
        {
            var secret = await secrets.ReadAsync(profile.SecretRef, linked.Token).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(secret)) throw Error("SecretUnavailable", "The configured model credential is unavailable.");
            var transport = httpClient is null
                ? new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false })
                : new HttpClient(new BorrowedTransport(httpClient));
            transport.Timeout = Timeout.InfiniteTimeSpan;
            sdk = new AnthropicClient(new ClientOptions
            {
                HttpClient = transport,
                BaseUrl = "https://api.anthropic.com",
                ApiKey = secret,
                AuthToken = null,
                Credentials = null,
                WebhookKey = null,
                MaxRetries = 0,
                Timeout = TimeSpan.FromSeconds(15),
                OpenTelemetry = false
            });
            // Read-only availability/auth check; aliases may legitimately resolve to a canonical model ID.
            var model = await sdk.Models.Retrieve(profile.ModelId, cancellationToken: linked.Token).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(model.ID)) throw Error("ModelUnavailable", "The provider did not confirm the selected model.");
            linked.Token.ThrowIfCancellationRequested();
            // The official MEAI bridge does not own/dispose the underlying SDK client.
            var client = new OwnedClient(sdk.AsIChatClient(profile.ModelId, profile.GenerationSettings.MaxOutputTokens ?? 4096), sdk);
            sdk = null;
            return new(client, profile);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (OperationCanceledException) { throw Error("ProviderTimedOut", "The model provider connection timed out."); }
        catch (ModelProviderException) { throw; }
        catch (AnthropicApiException exception)
        {
            throw (int)exception.StatusCode switch
            {
                401 or 403 => Error("ProviderAuthenticationFailed", "The model provider rejected the credential or access."),
                404 => Error("ModelUnavailable", "The selected model is unavailable."),
                429 => Error("ProviderRateLimited", "The model provider is rate limited."),
                _ => Error("ProviderUnavailable", "The model provider is unavailable; no automatic retry was performed.")
            };
        }
        catch (Exception) { throw Error("ProviderUnavailable", "The model provider or credential store is unavailable."); }
        finally { sdk?.Dispose(); }
    }

    public async Task<ModelConnectionTest> TestConnectionAsync(ModelProfile profile, CancellationToken cancellationToken)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        try
        {
            using var connection = await OpenAsync(profile, linked.Token).ConfigureAwait(false);
            var capabilities = await ModelCapabilityProbe.VerifyAsync(connection.Client, profile, linked.Token).ConfigureAwait(false);
            return new(true, null, "Provider/model access confirmed; only explicitly enabled capabilities were probed.", capabilities);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (OperationCanceledException) { return new(false, "ProviderTimedOut", "The model connection test timed out."); }
        catch (ModelProviderException exception) { return new(false, exception.Code, exception.Message, ProbeDiagnostic: exception.ProbeDiagnostic); }
        catch (Exception) { return new(false, "CapabilityProbeFailed", "The configured capability test failed; no action was executed or retried."); }
    }

    private void Validate(ModelProfile profile)
    {
        if (profile.ProviderId != ProviderId) throw Error("ProviderUnsupported", "This provider cannot open the selected profile.");
        if (profile.SchemaVersion != 1 || string.IsNullOrWhiteSpace(profile.Id) || string.IsNullOrWhiteSpace(profile.SecretRef)
            || profile.Capabilities is null || profile.GenerationSettings is null || !ModelIdPattern().IsMatch(profile.ModelId ?? "")
            || profile.GenerationSettings.MaxOutputTokens is <= 0
            || profile.GenerationSettings.Temperature is { } temperature && (!float.IsFinite(temperature) || temperature is < 0 or > 1))
            throw Error("ModelProfileInvalid", "The model profile configuration is invalid.");
        if (profile.Pricing is { } pricing && (pricing.InputPerMillionTokens < 0 || pricing.OutputPerMillionTokens < 0
            || pricing.Currency is not { Length: 3 } || pricing.Currency.Any(character => character is < 'A' or > 'Z')))
            throw Error("ModelProfileInvalid", "The configured model pricing is invalid.");
    }

    private sealed class OwnedClient(IChatClient inner, AnthropicClient sdk) : DelegatingChatClient(inner)
    {
        protected override void Dispose(bool disposing)
        {
            try { base.Dispose(disposing); }
            finally { if (disposing) sdk.Dispose(); }
        }
    }

    private sealed class BorrowedTransport(HttpClient client) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            // HttpClient marks each message as sent; a second client needs its own envelope.
            using var forwarded = new HttpRequestMessage(request.Method, request.RequestUri)
            { Version = request.Version, VersionPolicy = request.VersionPolicy, Content = request.Content };
            foreach (var header in request.Headers) forwarded.Headers.TryAddWithoutValidation(header.Key, header.Value);
            foreach (var option in request.Options) forwarded.Options.Set(new HttpRequestOptionsKey<object?>(option.Key), option.Value);
            try { return await client.SendAsync(forwarded, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false); }
            finally { forwarded.Content = null; } // The SDK retains ownership of the original content.
        }
    }
    private static ModelProviderException Error(string code, string message) => new(code, message);
    [GeneratedRegex("^[a-zA-Z0-9][a-zA-Z0-9._:-]{0,199}$", RegexOptions.CultureInvariant)]
    private static partial Regex ModelIdPattern();
}
