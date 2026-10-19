using Microsoft.Extensions.Hosting;
using PersonalAgent.Core.Agents;
using PersonalAgent.Core.Persistence;
using PersonalAgent.Core.Security;
using PersonalAgent.Mcp;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Windows.Threading;

namespace PersonalAgent.App.ViewModels;

public sealed record McpConnectionChoice(McpConnectionProfile Profile)
{
    public string Label => Profile.Configuration.Name + " (" + Profile.Configuration.Transport + ")";
}
public sealed class McpToolChoice(McpDiscoveredTool declaration, McpToolPermission? permission, bool allowed) : INotifyPropertyChanged
{
    private bool selected = allowed, independent = permission?.IndependentOfDesktop ?? false;
    private ToolRiskLevel risk = permission?.RiskLevel ?? ToolRiskLevel.Unknown;
    public McpDiscoveredTool Declaration { get; } = declaration;
    public string Name => Declaration.ServerName;
    public string Version => Declaration.Version;
    public string Description => Declaration.Descriptor.Description;
    public string Schema => Declaration.Descriptor.JsonSchema.GetRawText();
    public bool Allowed { get => selected; set { selected = value; Changed(); } }
    public ToolRiskLevel Risk { get => risk; set { risk = value; Changed(); } }
    public bool IndependentOfDesktop { get => independent; set { independent = value; Changed(); } }
    public event PropertyChangedEventHandler? PropertyChanged;
    private void Changed([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new(name));
}

public sealed class McpConnectionsViewModel : INotifyPropertyChanged, IDisposable
{
    private readonly Dispatcher dispatcher = Dispatcher.CurrentDispatcher;
    private readonly McpConnectionManager manager;
    private readonly ISecretStore secrets;
    private readonly IMcpConnectionRepository? repository;
    private readonly CancellationTokenSource lifetime;
    private McpConnectionChoice? selected;
    private McpToolChoice? selectedTool;
    private string name = "", endpoint = "https://", command = "", arguments = "[]", workingDirectory = "", environmentName = "MCP_API_TOKEN", pendingSecret = "";
    private McpTransportKind transport = McpTransportKind.Stdio;
    private bool enabled, approved, loopback, removeCredential, busy, disposed, loading, dirty;
    private string message = "Save settings, then explicitly connect to discover tools. Startup does not connect.";
    public McpConnectionsViewModel(McpConnectionManager manager, ISecretStore secrets, IHostApplicationLifetime host,
        IMcpConnectionRepository? repository = null)
    {
        this.manager = manager; this.secrets = secrets; this.repository = repository;
        lifetime = CancellationTokenSource.CreateLinkedTokenSource(host.ApplicationStopping);
        SaveCommand = new(SaveAsync, () => CanEdit);
        NewCommand = new(() => { Select(null); return Task.CompletedTask; }, () => CanEdit);
        ConnectCommand = new(ConnectAsync, () => CanEdit && !HasUnsavedChanges && selected?.Profile.Configuration is { Enabled: true, Approved: true });
        DisconnectCommand = new(DisconnectAsync, () => !disposed && !lifetime.IsCancellationRequested && selected is not null);
        RemoveCommand = new(RemoveAsync, () => CanEdit && selected is not null);
        SaveToolsCommand = new(SaveToolsAsync, () => CanEdit && !HasUnsavedChanges && selected is not null && Tools.Count > 0);
        manager.Changed += ConnectionChanged;
        Initialization = LoadAsync();
    }
    public event PropertyChangedEventHandler? PropertyChanged;
    public event Action? ClearSecretRequested;
    public ObservableCollection<McpConnectionChoice> Choices { get; } = [];
    public ObservableCollection<McpToolChoice> Tools { get; } = [];
    public IReadOnlyList<McpTransportKind> Transports { get; } = Enum.GetValues<McpTransportKind>();
    public IReadOnlyList<ToolRiskLevel> Risks { get; } = Enum.GetValues<ToolRiskLevel>();
    public Task Initialization { get; }
    public AsyncCommand SaveCommand { get; }
    public AsyncCommand NewCommand { get; }
    public AsyncCommand ConnectCommand { get; }
    public AsyncCommand DisconnectCommand { get; }
    public AsyncCommand RemoveCommand { get; }
    public AsyncCommand SaveToolsCommand { get; }
    public bool CanEdit => !busy && !disposed && !lifetime.IsCancellationRequested && repository is not null;
    public bool HasUnsavedChanges => selected is null || dirty || pendingSecret.Length != 0;
    public string SaveState => HasUnsavedChanges ? "Unsaved settings: save before connecting or reviewing permissions." : "Saved settings selected.";
    public string Message => message;
    public string ConnectionStatus => selected is null ? "Disconnected" : manager.List().FirstOrDefault(value => value.Id == selected.Profile.Configuration.Id)?.Phase.ToString() ?? "Disconnected";
    public bool IsHttp => Transport == McpTransportKind.StreamableHttp;
    public bool IsStdio => Transport == McpTransportKind.Stdio;
    public string Name { get => name; set => Set(ref name, value); }
    public string Endpoint { get => endpoint; set => Set(ref endpoint, value, true); }
    public string Command { get => command; set => Set(ref command, value, true); }
    public string ArgumentsJson { get => arguments; set => Set(ref arguments, value, true); }
    public string WorkingDirectory { get => workingDirectory; set => Set(ref workingDirectory, value, true); }
    public string CredentialEnvironmentName { get => environmentName; set => Set(ref environmentName, value, true); }
    public bool Enabled { get => enabled; set => Set(ref enabled, value); }
    public bool Approved { get => approved; set => Set(ref approved, value); }
    public bool AllowLoopbackHttp { get => loopback; set => Set(ref loopback, value, true); }
    public bool RemoveCredential { get => removeCredential; set => Set(ref removeCredential, value); }
    public McpTransportKind Transport
    {
        get => transport;
        set { Set(ref transport, value, true); Changed(nameof(IsHttp)); Changed(nameof(IsStdio)); }
    }
    public McpConnectionChoice? Selected { get => selected; set { if (!busy && !disposed) Select(value); } }
    public McpToolChoice? SelectedTool { get => selectedTool; set { selectedTool = value; Changed(); } }
    public void SetPendingSecret(string value) { pendingSecret = value; Refresh(); }

    private async Task LoadAsync()
    {
        if (repository is null) return;
        busy = true; Refresh();
        try { foreach (var profile in await repository.ListAsync(lifetime.Token)) Choices.Add(new(profile)); }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception) { message = "Saved MCP settings could not be loaded. No connection was started."; }
        finally { busy = false; Refresh(); }
    }
    private void Select(McpConnectionChoice? value)
    {
        selected = value; loading = true;
        try
        {
            var config = value?.Profile.Configuration;
            Name = config?.Name ?? ""; Transport = config?.Transport ?? McpTransportKind.Stdio;
            Endpoint = config?.Http?.Endpoint.AbsoluteUri ?? "https://";
            Command = config?.Stdio?.Command ?? ""; ArgumentsJson = JsonSerializer.Serialize(config?.Stdio?.Arguments ?? []);
            WorkingDirectory = config?.Stdio?.WorkingDirectory ?? "";
            CredentialEnvironmentName = config?.Stdio?.SecretEnvironment?.Keys.FirstOrDefault() ?? "MCP_API_TOKEN";
            Enabled = config?.Enabled ?? false; AllowLoopbackHttp = config?.Http?.AllowLoopbackHttp ?? false;
            Approved = config?.Approved ?? false; RemoveCredential = false; dirty = false; ClearSecret();
            Changed(nameof(Selected)); RenderTools();
        }
        finally { loading = false; Refresh(); }
    }
    private async Task SaveAsync()
    {
        busy = true; Refresh();
        try
        {
            var old = selected?.Profile;
            var id = old?.Configuration.Id ?? Guid.NewGuid();
            var target = new McpConnectionConfig { Id = id, Name = Name.Trim(), Transport = Transport, Enabled = Enabled, Approved = Approved,
                ConnectTimeout = old?.Configuration.ConnectTimeout ?? TimeSpan.FromSeconds(15), RequestTimeout = old?.Configuration.RequestTimeout ?? TimeSpan.FromSeconds(30),
                Http = IsHttp ? new(new Uri(Endpoint.Trim(), UriKind.Absolute), AllowLoopbackHttp: AllowLoopbackHttp) : null,
                Stdio = IsStdio ? new(Command.Trim(), JsonSerializer.Deserialize<string[]>(ArgumentsJson) ?? throw new FormatException(),
                    string.IsNullOrWhiteSpace(WorkingDirectory) ? null : WorkingDirectory.Trim()) : null };
            target = McpConnectionFactory.ValidateConfiguration(target);
            var sameTarget = SameTarget(old?.Configuration, target);
            var oldReferences = old?.Configuration.Stdio?.SecretEnvironment;
            var oldReference = old?.Configuration.Http?.BearerSecretRef ?? oldReferences?.GetValueOrDefault(CredentialEnvironmentName.Trim());
            if (!sameTarget && old is not null && (old.Configuration.Http?.BearerSecretRef is not null || oldReferences?.Count > 0)
                && !RemoveCredential && string.IsNullOrWhiteSpace(pendingSecret))
            { message = "Enter a credential for the changed destination, or select Remove saved credential. Existing credentials are not transferred."; return; }
            var reference = RemoveCredential ? null : !string.IsNullOrWhiteSpace(pendingSecret)
                ? await secrets.StoreAsync(pendingSecret, lifetime.Token) : sameTarget ? oldReference : null;
            if (IsHttp) target = target with { Http = target.Http! with { BearerSecretRef = reference } };
            else
            {
                var bindings = sameTarget && !RemoveCredential && oldReferences is not null ? oldReferences.ToDictionary() : new Dictionary<string, string>();
                if (reference is not null)
                {
                    if (string.IsNullOrWhiteSpace(CredentialEnvironmentName)) { message = "Enter a non-system environment name for the protected credential."; return; }
                    bindings[CredentialEnvironmentName.Trim()] = reference;
                }
                target = target with { Stdio = target.Stdio! with { SecretEnvironment = bindings.Count == 0 ? null : bindings } };
            }
            target = McpConnectionFactory.ValidateConfiguration(target with { AllowedTools = sameTarget ? old!.Configuration.AllowedTools : [] });
            var profile = new McpConnectionProfile { Configuration = target, Permissions = sameTarget ? old!.Permissions : [] };
            await repository!.SaveAsync(profile, lifetime.Token);
            await manager.DisconnectAsync(id, lifetime.Token);
            Replace(profile);
            message = "MCP settings saved. Connect and discover uses only the saved, enabled and approved destination.";
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { message = "Application is stopping."; }
        catch (Exception) { message = "MCP settings could not be saved. Check the name, transport, absolute executable/URL and JSON argument array."; }
        finally { ClearSecret(); busy = false; Refresh(); }
    }
    private async Task ConnectAsync()
    {
        var profile = selected?.Profile; if (profile is null) return;
        busy = true; Refresh();
        try
        {
            var status = await manager.ConnectAsync(profile.Configuration, lifetime.Token);
            RenderTools();
            message = status.Phase == McpConnectionPhase.Connected ? "Connected. Discovered tools require explicit owner classification and selection."
                : "Connection is " + status.Phase + ": " + (status.ErrorCode ?? "Unavailable") + ".";
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { message = "Connection cancelled: application is stopping."; }
        catch (Exception) { message = "MCP connection could not be tested. No tool call was executed."; }
        finally { busy = false; Refresh(); }
    }
    private async Task DisconnectAsync()
    {
        if (selected is not { } value) return;
        try { await manager.DisconnectAsync(value.Profile.Configuration.Id, lifetime.Token); message = "Disconnected. Tools are unavailable."; }
        catch (Exception) { message = "Connection cleanup needs review. Tools remain unavailable."; }
        finally { RenderTools(); Refresh(); }
    }
    private async Task RemoveAsync()
    {
        if (selected is not { } value) return;
        busy = true; Refresh();
        try
        {
            await manager.RemoveAsync(value.Profile.Configuration.Id, lifetime.Token);
            if (manager.List().Any(status => status.Id == value.Profile.Configuration.Id)) { message = "Cleanup is unconfirmed; saved connection retained for review."; return; }
            await repository!.DeleteAsync(value.Profile.Configuration.Id, lifetime.Token); Choices.Remove(value); Select(null);
            message = "Connection removed. Protected credential values were not displayed.";
        }
        catch (Exception) { message = "Connection could not be removed. Check cleanup and storage."; }
        finally { busy = false; Refresh(); }
    }
    private async Task SaveToolsAsync()
    {
        var profile = selected?.Profile; if (profile is null) return;
        var enabledTools = Tools.Where(tool => tool.Allowed).ToArray();
        if (enabledTools.Any(tool => tool.Risk == ToolRiskLevel.Unknown)) { message = "Choose a risk classification for every selected tool. Server hints do not grant permission."; Refresh(); return; }
        busy = true; Refresh();
        try
        {
            var configuration = profile.Configuration with { AllowedTools = Array.AsReadOnly(enabledTools.Select(tool => tool.Name).ToArray()) };
            var updated = profile with { Configuration = configuration, Permissions = Array.AsReadOnly(enabledTools.Select(tool => new McpToolPermission(
                configuration.Id, tool.Declaration.Descriptor.Id, tool.Version, tool.Risk, tool.IndependentOfDesktop)).ToArray()) };
            await repository!.SaveAsync(updated, lifetime.Token);
            await manager.DisconnectAsync(configuration.Id, lifetime.Token);
            Replace(updated); message = "Tool selection and reviewed versions saved. Connect again to apply them; sensitive actions still require per-run authorization.";
        }
        catch (Exception) { message = "Tool permissions could not be saved. No tool was invoked."; }
        finally { busy = false; Refresh(); }
    }
    private static bool SameTarget(McpConnectionConfig? old, McpConnectionConfig next) => old is not null && old.Transport == next.Transport
        && (next.Transport == McpTransportKind.StreamableHttp ? old.Http?.Endpoint == next.Http?.Endpoint
            : old.Stdio?.Command == next.Stdio?.Command && old.Stdio?.WorkingDirectory == next.Stdio?.WorkingDirectory
                && old.Stdio!.Arguments.SequenceEqual(next.Stdio!.Arguments, StringComparer.Ordinal));
    private void Replace(McpConnectionProfile profile)
    {
        var old = Choices.FirstOrDefault(choice => choice.Profile.Configuration.Id == profile.Configuration.Id);
        if (old is not null) Choices.Remove(old);
        var choice = new McpConnectionChoice(profile); Choices.Add(choice); Select(choice);
    }
    private void ConnectionChanged(McpConnectionStatus status)
    {
        if (disposed || dispatcher.HasShutdownStarted) return;
        dispatcher.BeginInvoke(new Action(() => { if (!disposed && selected?.Profile.Configuration.Id == status.Id) { RenderTools(); Refresh(); } }));
    }
    private void RenderTools()
    {
        var profile = selected?.Profile;
        var discovered = manager.List().FirstOrDefault(value => value.Id == profile?.Configuration.Id)?.Tools ?? [];
        var versions = Tools.Select(tool => tool.Declaration.Descriptor.Id + ":" + tool.Version).ToArray();
        if (versions.SequenceEqual(discovered.Select(tool => tool.Descriptor.Id + ":" + tool.Version), StringComparer.Ordinal)) return;
        Tools.Clear(); SelectedTool = null;
        foreach (var declaration in discovered)
        {
            var permission = profile!.Permissions.FirstOrDefault(value => value.ToolId == declaration.Descriptor.Id && value.Version == declaration.Version);
            Tools.Add(new(declaration, permission, permission is not null && profile.Configuration.AllowedTools.Contains(declaration.ServerName, StringComparer.Ordinal)));
        }
        SelectedTool = Tools.FirstOrDefault();
    }
    private void Set<T>(ref T field, T value, bool target = false, [CallerMemberName] string? property = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value; Changed(property);
        if (!loading) { dirty = true; if (target) { approved = false; Changed(nameof(Approved)); } }
        Refresh();
    }
    private void ClearSecret() { pendingSecret = ""; ClearSecretRequested?.Invoke(); }
    private void Changed([CallerMemberName] string? property = null) => PropertyChanged?.Invoke(this, new(property));
    private void Refresh()
    {
        Changed(nameof(CanEdit)); Changed(nameof(HasUnsavedChanges)); Changed(nameof(SaveState)); Changed(nameof(Message)); Changed(nameof(ConnectionStatus));
        SaveCommand.Refresh(); NewCommand.Refresh(); ConnectCommand.Refresh(); DisconnectCommand.Refresh(); RemoveCommand.Refresh(); SaveToolsCommand.Refresh();
    }
    public void Dispose() { if (disposed) return; disposed = true; manager.Changed -= ConnectionChanged; lifetime.Cancel(); ClearSecret(); lifetime.Dispose(); Refresh(); }
}
