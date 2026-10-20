using System.Windows.Threading;
using PersonalAgent.Core.Channels;

namespace PersonalAgent.App;

public interface IDesktopNotificationSink
{
    Task ShowAsync(string text, CancellationToken ct);
}

// Actual tray/dispatcher is attached only by attended App startup. A headless host creates no OS UI.
public sealed class DesktopNotificationSink : IDesktopNotificationSink
{
    private readonly object sync = new();
    private Dispatcher? dispatcher;
    private ITrayIcon? tray;
    public void Attach(Dispatcher ui, ITrayIcon surface) { lock (sync) { dispatcher = ui; tray = surface; } }
    public void Detach() { lock (sync) { dispatcher = null; tray = null; } }
    public async Task ShowAsync(string text, CancellationToken ct)
    {
        Dispatcher ui; ITrayIcon surface;
        lock (sync)
        {
            ui = dispatcher ?? throw new ChannelDeliveryException("DesktopUnavailable", false, true);
            surface = tray ?? throw new ChannelDeliveryException("DesktopUnavailable", false, true);
        }
        if (ui.HasShutdownStarted) throw new ChannelDeliveryException("DesktopUnavailable", false, true);
        await ui.InvokeAsync(() =>
        {
            ct.ThrowIfCancellationRequested();
            lock (sync)
            {
                if (dispatcher != ui || tray != surface) throw new ChannelDeliveryException("DesktopUnavailable", false, true);
                surface.ShowNotification(text);
            }
        }, DispatcherPriority.Background, ct).Task.ConfigureAwait(false);
    }
}

public sealed class DesktopOutboundChannel(string destinationId, IChannelAccessPolicy policy, IDesktopNotificationSink sink) : IOutboundChannel
{
    public string ChannelId => "desktop";
    public string ConnectionId => "local";
    public ChannelCapabilities Capabilities { get; } = new(false);
    public async Task SendAsync(OutgoingMessage incoming, CancellationToken ct)
    {
        var message = incoming.Snapshot(); ct.ThrowIfCancellationRequested();
        if (message.Destination != new ChannelRoute(ChannelId, ConnectionId, destinationId) || !await policy.AllowOutboundAsync(message, ct).ConfigureAwait(false))
            throw new ChannelAccessDeniedException();
        if (message.Actions.Count > 0) throw new NotSupportedException("Desktop tray actions require local app review.");
        ct.ThrowIfCancellationRequested();
        await sink.ShowAsync(Preview(message.Text), ct).ConfigureAwait(false);
    }
    public static string Preview(string text)
    {
        const int limit = 240;
        if (text.Length <= limit) return text;
        var length = limit; if (char.IsHighSurrogate(text[length - 1])) length--;
        return text[..length] + "…";
    }
}
