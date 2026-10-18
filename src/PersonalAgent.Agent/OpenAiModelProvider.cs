using Microsoft.Extensions.AI;
using OpenAI;
using PersonalAgent.Core.Agents;
using PersonalAgent.Core.Security;
using System.ClientModel;
using System.ClientModel.Primitives;
using System.Text.RegularExpressions;

namespace PersonalAgent.Agent;

// The optional transport is supplied only by the trusted composition root/tests, never a model/profile.
public sealed partial class OpenAiModelProvider(ISecretStore secrets, HttpClient? httpClient = null) : IModelProvider
{
    private static readonly HttpClient DefaultHttpClient = new(new SocketsHttpHandler { AllowAutoRedirect = false })
    { Timeout = Timeout.InfiniteTimeSpan };
    public string ProviderId => "openai";
    public async Task<ModelConnection> OpenAsync(ModelProfile profile, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Validate(profile);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        try
        {
            var secret = await secrets.ReadAsync(profile.SecretRef, linked.Token).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(secret)) throw Error("SecretUnavailable", "The configured model credential is unavailable.");
            var options = new OpenAIClientOptions
            {
                Endpoint = new Uri("https://api.openai.com/v1"),
                RetryPolicy = new ClientRetryPolicy(0),
                NetworkTimeout = TimeSpan.FromSeconds(15),
                ClientLoggingOptions = new() { EnableLogging = false },
                EnableDistributedTracing = false
            };
            options.Transport = new HttpClientPipelineTransport(httpClient ?? DefaultHttpClient);
            var sdk = new OpenAIClient(new ApiKeyCredential(secret), options);
            // Model availability/auth check is read-only and does not generate paid output or infer capabilities.
            var model = await sdk.GetOpenAIModelClient().GetModelAsync(profile.ModelId, linked.Token).ConfigureAwait(false);
            if (model.Value.Id != profile.ModelId) throw Error("ModelUnavailable", "The provider did not confirm the selected model.");
            linked.Token.ThrowIfCancellationRequested();
            // Stable pinned SDK chat bridge; no experimental Responses contract is required by this slice.
            return new(sdk.GetChatClient(profile.ModelId).AsIChatClient(), profile);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (OperationCanceledException) { throw Error("ProviderTimedOut", "The model provider connection timed out."); }
        catch (ModelProviderException) { throw; }
        catch (ClientResultException exception)
        {
            throw exception.Status switch
            {
                401 or 403 => Error("ProviderAuthenticationFailed", "The model provider rejected the credential or access."),
                404 => Error("ModelUnavailable", "The selected model is unavailable."),
                429 => Error("ProviderRateLimited", "The model provider is rate limited."),
                _ => Error("ProviderUnavailable", "The model provider is unavailable; no automatic retry was performed.")
            };
        }
        catch (Exception) { throw Error("ProviderUnavailable", "The model provider or credential store is unavailable."); }
    }

    public async Task<ModelConnectionTest> TestConnectionAsync(ModelProfile profile, CancellationToken cancellationToken)
    {
        try
        {
            using var connection = await OpenAsync(profile, cancellationToken).ConfigureAwait(false);
            return new(true, null, "Provider authentication and model availability confirmed; capabilities have not been probed.");
        }
        catch (ModelProviderException exception) { return new(false, exception.Code, exception.Message); }
    }

    private void Validate(ModelProfile profile)
    {
        if (profile.ProviderId != ProviderId) throw Error("ProviderUnsupported", "This provider cannot open the selected profile.");
        if (profile.SchemaVersion != 1 || string.IsNullOrWhiteSpace(profile.Id) || string.IsNullOrWhiteSpace(profile.SecretRef)
            || profile.Capabilities is null || profile.GenerationSettings is null
            || !ModelIdPattern().IsMatch(profile.ModelId ?? "")
            || profile.GenerationSettings.MaxOutputTokens is <= 0
            || profile.GenerationSettings.Temperature is { } temperature && (!float.IsFinite(temperature) || temperature is < 0 or > 2))
            throw Error("ModelProfileInvalid", "The model profile configuration is invalid.");
    }
    private static ModelProviderException Error(string code, string message) => new(code, message);
    [GeneratedRegex("^[a-zA-Z0-9][a-zA-Z0-9._:-]{0,199}$", RegexOptions.CultureInvariant)]
    private static partial Regex ModelIdPattern();
}
