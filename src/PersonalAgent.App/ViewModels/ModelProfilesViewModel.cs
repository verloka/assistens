using Microsoft.Extensions.Hosting;
using PersonalAgent.Agent;
using PersonalAgent.Core.Agents;
using PersonalAgent.Core.Persistence;
using PersonalAgent.Core.Security;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;

namespace PersonalAgent.App.ViewModels;

public sealed record ModelProfileChoice(string Label, ModelProfile? Profile);

public sealed class ModelProfilesViewModel : INotifyPropertyChanged, IDisposable
{
    private readonly ISecretStore secrets;
    private readonly IModelProfileRepository? repository;
    private readonly IReadOnlyDictionary<string, IModelProvider> providers;
    private readonly CancellationTokenSource lifetime;
    private ModelProfileChoice? selected;
    private string profileId = "", providerId = "openai", modelId = "", pendingSecret = "";
    private bool images, tools = true, busy, disposed;
    private int outputTokens = 4096;
    private string message = "Local demo selected. Cloud tasks use the selected provider API.";

    public ModelProfilesViewModel(ISecretStore secrets, IEnumerable<IModelProvider> providers, IHostApplicationLifetime host,
        IModelProfileRepository? repository = null)
    {
        this.secrets = secrets; this.repository = repository;
        this.providers = providers.GroupBy(p => p.ProviderId).ToDictionary(g => g.Key, g => g.Last());
        ProviderIds = this.providers.Keys.Order(StringComparer.Ordinal).ToArray();
        lifetime = CancellationTokenSource.CreateLinkedTokenSource(host.ApplicationStopping);
        SaveCommand = new AsyncCommand(SaveAsync, () => CanEdit && repository is not null);
        TestCommand = new AsyncCommand(TestAsync, () => CanEdit && Selected?.Profile is not null);
        NewCommand = new AsyncCommand(() => { ResetEditor(); return Task.CompletedTask; }, () => CanEdit && repository is not null);
        Choices.Add(new("Local demo (no API)", null)); selected = Choices[0];
        Initialization = LoadAsync();
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    public event Action? ClearSecretRequested;
    public ObservableCollection<ModelProfileChoice> Choices { get; } = [];
    public IReadOnlyList<string> ProviderIds { get; }
    public Task Initialization { get; }
    public AsyncCommand SaveCommand { get; }
    public AsyncCommand TestCommand { get; }
    public AsyncCommand NewCommand { get; }
    public bool CanEdit => !busy && !disposed && !lifetime.IsCancellationRequested;
    public string Message => message;
    public string ProfileId { get => profileId; set { profileId = value; Changed(); } }
    public string ProviderId { get => providerId; set { providerId = value; Changed(); } }
    public string ModelId { get => modelId; set { modelId = value; Changed(); } }
    public bool SupportsImages { get => images; set { images = value; Changed(); } }
    public bool SupportsToolCalling { get => tools; set { tools = value; Changed(); } }
    public int MaxOutputTokens { get => outputTokens; set { outputTokens = value; Changed(); } }
    public ModelProfileChoice? Selected
    {
        get => selected;
        set
        {
            if (!CanEdit || value is null || !Choices.Contains(value)) return;
            selected = value; Changed();
            if (value.Profile is { } profile)
            {
                ProfileId = profile.Id; ProviderId = profile.ProviderId; ModelId = profile.ModelId;
                SupportsImages = profile.Capabilities.SupportsImages; SupportsToolCalling = profile.Capabilities.SupportsToolCalling;
                MaxOutputTokens = profile.GenerationSettings.MaxOutputTokens ?? 4096;
            }
            else ResetEditor();
            ClearSecret(); Refresh();
        }
    }
    // Transient password input is never a bindable/persisted profile property.
    public void SetPendingSecret(string secret) => pendingSecret = secret;

    private async Task LoadAsync()
    {
        if (repository is null) return;
        busy = true; Refresh();
        try { foreach (var profile in await repository.ListAsync(lifetime.Token)) Choices.Add(Choice(profile)); }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception) { message = "Model profiles could not be loaded. Local demo remains available."; }
        finally { busy = false; Refresh(); }
    }

    private async Task SaveAsync()
    {
        busy = true; Refresh();
        try
        {
            var id = ProfileId.Trim(); var model = ModelId.Trim(); var provider = ProviderId;
            if (!Regex.IsMatch(id, "^[a-zA-Z0-9][a-zA-Z0-9._:-]{0,99}$") || id == "fake"
                || !Regex.IsMatch(model, "^[a-zA-Z0-9][a-zA-Z0-9._:-]{0,199}$")
                || !providers.ContainsKey(provider) || MaxOutputTokens <= 0)
            { message = "Enter a valid profile ID, provider, model ID and positive output limit."; return; }
            var existing = Choices.Select(c => c.Profile).FirstOrDefault(p => p?.Id == id);
            var secret = pendingSecret;
            if (string.IsNullOrWhiteSpace(secret) && (existing is null || existing.ProviderId != provider))
            { message = "Enter an API key for a new profile or a changed provider."; return; }
            var snapshot = new ModelProfile
            {
                Id = id, ProviderId = provider, ModelId = model,
                SecretRef = string.IsNullOrWhiteSpace(secret) ? existing!.SecretRef : await secrets.StoreAsync(secret, lifetime.Token),
                Capabilities = new(SupportsImages, SupportsToolCalling),
                GenerationSettings = new(existing?.ProviderId == provider ? existing.GenerationSettings.Temperature : null, MaxOutputTokens),
                Pricing = existing?.ProviderId == provider && existing.ModelId == model ? existing.Pricing : null
            };
            await repository!.SaveAsync(snapshot, lifetime.Token);
            var old = Choices.FirstOrDefault(c => c.Profile?.Id == id);
            if (old is not null) Choices.Remove(old);
            selected = Choice(snapshot); Choices.Add(selected); Changed(nameof(Selected));
            message = "Profile saved and selected for the next task. Test saved profile checks the provider API.";
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { message = "Application is stopping."; }
        catch (Exception) { message = "Profile could not be saved. No task was started."; }
        finally { ClearSecret(); busy = false; Refresh(); }
    }

    private async Task TestAsync()
    {
        var profile = Selected?.Profile;
        if (profile is null) return;
        busy = true; Refresh();
        try
        {
            var result = await providers[profile.ProviderId].TestConnectionAsync(profile, lifetime.Token);
            message = result.Connected ? "Connection and configured capabilities confirmed." : "Connection test failed: " + (result.ErrorCode ?? "Unavailable") + ".";
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { message = "Connection test cancelled: application is stopping."; }
        catch (Exception) { message = "Connection test failed. Check the saved profile and credential."; }
        finally { busy = false; Refresh(); }
    }

    private void ResetEditor()
    {
        ProfileId = ""; ProviderId = "openai"; ModelId = ""; SupportsImages = false; SupportsToolCalling = true; MaxOutputTokens = 4096;
        ClearSecret();
    }
    private void ClearSecret() { pendingSecret = ""; ClearSecretRequested?.Invoke(); }
    private static ModelProfileChoice Choice(ModelProfile profile) => new($"{profile.Id}: {profile.ProviderId} / {profile.ModelId}", profile);
    private void Changed([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new(name));
    private void Refresh() { Changed(nameof(CanEdit)); Changed(nameof(Message)); SaveCommand.Refresh(); TestCommand.Refresh(); NewCommand.Refresh(); }
    public void Dispose() { if (disposed) return; disposed = true; ClearSecret(); lifetime.Cancel(); lifetime.Dispose(); }
}
