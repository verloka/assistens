using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.Hosting;
using PersonalAgent.Channels.Telegram;
using PersonalAgent.Core.Channels;
using PersonalAgent.Core.Persistence;
using PersonalAgent.Core.Security;

namespace PersonalAgent.App.ViewModels;

public sealed class NotificationEventChoice(NotificationEventKind kind) : INotifyPropertyChanged
{
    private bool selected;
    public NotificationEventKind Kind => kind;
    public bool Selected { get => selected; set { selected = value; PropertyChanged?.Invoke(this, new(nameof(Selected))); } }
    public event PropertyChangedEventHandler? PropertyChanged;
}

public sealed class ChannelsViewModel : INotifyPropertyChanged, IDisposable
{
    private readonly ApprovalOwner owner;
    private readonly ISecretStore secrets;
    private readonly TelegramConnectionProbe probe;
    private readonly IChannelConfigurationRepository? repository;
    private readonly IChannelBindingRepository? bindings;
    private readonly CancellationTokenSource lifetime;
    private ChannelConfiguration? selected;
    private string name = "Telegram bot", kind = "telegram", destination = "", sender = "", pendingSecret = "", message = "Save disabled, test the saved bot, review its peer, then enable.";
    private bool enabled, receive, authorized, busy, dirty, disposed;
    public ChannelsViewModel(ApprovalOwner owner, ISecretStore secrets, TelegramConnectionProbe probe, IHostApplicationLifetime host,
        IChannelConfigurationRepository? repository = null, IChannelBindingRepository? bindings = null)
    {
        this.owner = owner; this.secrets = secrets; this.probe = probe; this.repository = repository; this.bindings = bindings;
        lifetime = CancellationTokenSource.CreateLinkedTokenSource(host.ApplicationStopping);
        foreach (var value in Enum.GetValues<NotificationEventKind>().Where(value => value != NotificationEventKind.ImportantFinding))
        { var choice = new NotificationEventChoice(value); choice.PropertyChanged += (_, _) => Edit(); Events.Add(choice); }
        SaveCommand = new(SaveAsync, () => CanEdit && repository is not null && bindings is not null);
        TestCommand = new(TestAsync, () => CanEdit && Selected is not null && !dirty);
        NewCommand = new(() => { Load(null); return Task.CompletedTask; }, () => CanEdit);
        RefreshCommand = new(LoadAsync, () => CanEdit && repository is not null);
        Initialization = LoadAsync();
    }
    public event PropertyChangedEventHandler? PropertyChanged;
    public event Action? ClearSecretRequested;
    public ObservableCollection<ChannelConfiguration> Connections { get; } = [];
    public ObservableCollection<NotificationEventChoice> Events { get; } = [];
    public IReadOnlyList<string> Kinds { get; } = ["telegram", "desktop"];
    public Task Initialization { get; }
    public AsyncCommand SaveCommand { get; }
    public AsyncCommand TestCommand { get; }
    public AsyncCommand NewCommand { get; }
    public AsyncCommand RefreshCommand { get; }
    public bool CanEdit => !busy && !disposed && !lifetime.IsCancellationRequested;
    public bool CanChangeIdentity => CanEdit && selected is null;
    public string Message => message;
    public string SaveState => dirty ? "Unsaved changes. Save before testing." : "Tests use saved settings. A new token or peer requires a new connection.";
    public ChannelConfiguration? Selected { get => selected; set { if (CanEdit && value is not null && Connections.Contains(value)) Load(value); } }
    public string Name { get => name; set { name = value; Edit(); } }
    public string Kind { get => kind; set { kind = value; Edit(); } }
    public string Destination { get => destination; set { destination = value; Edit(); } }
    public string Sender { get => sender; set { sender = value; Edit(); } }
    public bool Enabled { get => enabled; set { enabled = value; Edit(); } }
    public bool ReceiveTasks { get => receive; set { receive = value; Edit(); } }
    public bool AuthorizePeer { get => authorized; set { authorized = value; Edit(); } }
    public void SetPendingSecret(string value) { pendingSecret = value; Edit(); }
    public void ClearSecret() { pendingSecret = ""; ClearSecretRequested?.Invoke(); }

    private async Task LoadAsync()
    {
        if (repository is null) { message = "Persistent history is required to configure channels."; Refresh(); return; }
        busy = true; Refresh();
        try
        {
            var prior = selected?.Id; var rows = await repository.ListAsync(owner, lifetime.Token);
            Connections.Clear(); foreach (var row in rows) Connections.Add(row);
            Load(Connections.SingleOrDefault(item => item.Id == prior));
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { message = "Application is stopping."; }
        catch (Exception) { message = "Channel settings could not be loaded. Existing permissions were not changed."; }
        finally { busy = false; Refresh(); }
    }
    private void Load(ChannelConfiguration? value)
    {
        selected = value; name = value?.Name ?? "Telegram bot"; kind = value?.Route.ChannelId ?? "telegram";
        destination = value?.Route.DestinationId ?? ""; sender = value?.SenderId ?? "";
        enabled = value?.Enabled ?? false; receive = value?.ReceiveTasks ?? false; authorized = value?.PeerAuthorized ?? false;
        foreach (var choice in Events) choice.Selected = value?.Events.Contains(choice.Kind) ?? choice.Kind == NotificationEventKind.Completed;
        ClearSecret(); dirty = false;
        foreach (var property in new[] { nameof(Selected), nameof(Name), nameof(Kind), nameof(Destination), nameof(Sender), nameof(Enabled), nameof(ReceiveTasks), nameof(AuthorizePeer) }) Changed(property);
        Refresh();
    }
    private async Task SaveAsync()
    {
        busy = true; Refresh();
        try
        {
            var current = selected; var id = current?.Id ?? Guid.NewGuid(); var channel = Kind;
            var events = Events.Where(item => item.Selected).Select(item => item.Kind).ToArray();
            var peerAllowed = AuthorizePeer; var requestedEnable = Enabled; var acceptTasks = ReceiveTasks;
            var title = Name.Trim(); var secret = pendingSecret;
            if (string.IsNullOrWhiteSpace(title) || title.Length > 128 || title.Any(char.IsControl)) { message = "Enter a channel name of up to 128 characters."; return; }
            ChannelRoute route; string peer;
            if (channel == "desktop") { route = new("desktop", "local", owner.UserId); peer = owner.UserId; acceptTasks = false; }
            else if (channel == "telegram" && long.TryParse(Destination, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var chat) && chat != 0
                && long.TryParse(Sender, NumberStyles.None, CultureInfo.InvariantCulture, out var user) && user > 0)
            { route = new("telegram", id.ToString("N"), chat.ToString(CultureInfo.InvariantCulture)); peer = user.ToString(CultureInfo.InvariantCulture); }
            else { message = "Enter a numeric Telegram chat ID and a positive numeric user ID."; return; }
            if (current is not null && (current.Route != route || current.SenderId != peer || secret.Length > 0))
            { message = "Create a new connection to change its token, channel or peer. Disable the old connection first."; return; }
            if (channel == "telegram" && current is null && (secret.Length > 256 || !System.Text.RegularExpressions.Regex.IsMatch(secret, "^[0-9]+:[a-zA-Z0-9_-]+$")))
            { message = "Enter a valid bot token. It is stored only in the protected local secret store."; return; }
            var reference = current?.SecretReference;
            if (channel == "telegram" && current is null) reference = await secrets.StoreAsync(secret, lifetime.Token);
            var canEnable = requestedEnable && peerAllowed && (channel == "desktop" || current?.RemoteIdentity is not null);
            var since = current?.SinceUtc ?? DateTimeOffset.UtcNow;
            if (canEnable && current?.Enabled != true) since = DateTimeOffset.UtcNow;
            var configuration = new ChannelConfiguration(id, owner, title, route, peer, canEnable, acceptTasks, peerAllowed, reference,
                events, since, current?.Version ?? 0, current?.RemoteIdentity, RemoteUsername: current?.RemoteUsername).Snapshot();
            var saved = await repository!.SaveAsync(configuration, configuration.Version, lifetime.Token);
            // Configuration gates close before revocation, and a failed pairing update cannot grant authority.
            var binding = await bindings!.RequestPairingAsync(saved.Route, saved.SenderId, owner, lifetime.Token);
            if ((binding.Status == ChannelBindingStatus.Approved) != saved.PeerAuthorized)
                await bindings.SetStatusAsync(binding.Id, binding.Version, owner, saved.PeerAuthorized, lifetime.Token);
            Replace(saved); Load(saved);
            message = channel == "telegram" && saved.RemoteIdentity is null ? "Saved disabled. Test saved connection, then explicitly enable after reviewing the user/chat."
                : saved.Enabled ? "Channel enabled with the saved peer and selected events." : "Channel saved disabled.";
        }
        catch (ChannelConfigurationConflictException) { message = "Settings changed or this bot is already enabled elsewhere. Refresh and review before saving."; }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { message = "Application is stopping."; }
        catch (ArgumentException) { message = "Check the name, channel, peer and event settings."; }
        catch (Exception) { message = "Settings or peer approval could not be saved. Refresh and review; no connection was implicitly enabled."; }
        finally { ClearSecret(); busy = false; Refresh(); }
    }
    private async Task TestAsync()
    {
        var saved = selected; if (saved is null) return;
        busy = true; Refresh();
        try
        {
            if (saved.Route.ChannelId == "desktop") { message = "Desktop uses the existing local tray. Enable notifications to allow future selected events."; return; }
            var current = (await repository!.ListAsync(owner, lifetime.Token)).SingleOrDefault(item => item.Id == saved.Id);
            if (current is null || current.Version != saved.Version || current.SecretReference is null) throw new ChannelConfigurationConflictException();
            var result = await probe.TestAsync(current.SecretReference, lifetime.Token);
            if (!result.Connected)
            {
                message = result.Code switch
                {
                    "Unauthorized" => "Bot token was rejected. Create a new connection with a current token.",
                    "MissingOrInvalidCredential" => "Protected bot token is missing or invalid. Create a new connection.",
                    "NetworkUnavailable" or "Timeout" => "Telegram is unavailable. Check connectivity and retry the saved test.",
                    "RateLimited" => "Telegram rate limited the test. Wait before testing again.",
                    _ => "Telegram returned an invalid or rejected response. Review the saved bot configuration."
                }; return;
            }
            var verified = await repository.SaveAsync(current with { RemoteIdentity = result.BotId, RemoteUsername = result.BotUsername }, current.Version, lifetime.Token);
            Replace(verified); Load(verified); message = "Bot token verified. This does not test chat membership or grant tools. Review the peer, then explicitly enable.";
        }
        catch (ChannelConfigurationConflictException) { message = "Saved settings changed during the test. Refresh and review."; }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { message = "Connection test cancelled."; }
        catch (Exception) { message = "Connection test failed. Check the protected saved credential and connectivity."; }
        finally { busy = false; Refresh(); }
    }
    private void Replace(ChannelConfiguration value) { var prior = Connections.SingleOrDefault(item => item.Id == value.Id); if (prior is not null) Connections.Remove(prior); Connections.Add(value); }
    private void Edit([CallerMemberName] string? property = null) { dirty = true; Changed(property); Refresh(); }
    private void Changed([CallerMemberName] string? property = null) => PropertyChanged?.Invoke(this, new(property));
    private void Refresh()
    {
        Changed(nameof(CanEdit)); Changed(nameof(CanChangeIdentity)); Changed(nameof(Message)); Changed(nameof(SaveState));
        SaveCommand?.Refresh(); TestCommand?.Refresh(); NewCommand?.Refresh(); RefreshCommand?.Refresh();
    }
    public void Dispose() { if (disposed) return; disposed = true; lifetime.Cancel(); lifetime.Dispose(); ClearSecret(); }
}
