using System.Windows.Input;
using PersonalAgent.App.ViewModels;
using Forms = System.Windows.Forms;

namespace PersonalAgent.App;

// Only the real desktop startup creates this OS resource; headless hosts/tests never do.
public interface ITrayIcon : IDisposable
{
    void Start(ControlPanelViewModel model, Action open, Action controls, Action exit);
    void Refresh(string status);
    void ShowNotification(string text) => throw new PersonalAgent.Core.Channels.ChannelDeliveryException("DesktopUnavailable", false, true);
}

public sealed class WindowsTrayIcon : ITrayIcon
{
    private Forms.NotifyIcon? icon;
    private Forms.ContextMenuStrip? menu;
    private System.Drawing.Icon? ownedIcon;
    private readonly List<(Forms.ToolStripMenuItem Item, ICommand Command)> commands = [];
    public void Start(ControlPanelViewModel model, Action open, Action controls, Action exit)
    {
        if (icon is not null) return;
        menu = new Forms.ContextMenuStrip();
        menu.Items.Add("Open", null, (_, _) => open());
        menu.Items.Add("Controls", null, (_, _) => controls());
        AddCommand("Pause & Take Control", model.TakeControlCommand);
        AddCommand("Resume", model.ResumeCommand);
        AddCommand("Cancel", model.CancelCommand);
        menu.Items.Add("Exit", null, (_, _) => exit());
        menu.Opening += (_, _) => { model.Refresh(); Refresh(model.Status); };
        using var resource = System.Windows.Application.GetResourceStream(new Uri("/PersonalAgent.App;component/Assets/agent.ico", UriKind.Relative)).Stream;
        using var decoded = new System.Drawing.Icon(resource, Forms.SystemInformation.SmallIconSize);
        ownedIcon = (System.Drawing.Icon)decoded.Clone();
        icon = new Forms.NotifyIcon { Icon = ownedIcon, Text = "Personal Agent", ContextMenuStrip = menu, Visible = true };
        icon.DoubleClick += (_, _) => open();
        icon.BalloonTipClicked += (_, _) => open(); // Only a deliberate click opens/activates the main window.
        Refresh(model.Status);
    }
    private void AddCommand(string title, ICommand command)
    {
        var item = new Forms.ToolStripMenuItem(title);
        item.Click += (_, _) => { if (command.CanExecute(null)) command.Execute(null); };
        commands.Add((item, command)); menu!.Items.Add(item);
    }
    public void Refresh(string status)
    {
        if (icon is null) return;
        icon.Text = $"Personal Agent: {status}";
        foreach (var (item, command) in commands) item.Enabled = command.CanExecute(null);
    }
    public void Dispose()
    {
        if (icon is not null) { icon.Visible = false; icon.Dispose(); icon = null; }
        ownedIcon?.Dispose(); ownedIcon = null;
        menu?.Dispose(); menu = null; commands.Clear();
    }
    public void ShowNotification(string text)
    {
        if (icon is null) throw new PersonalAgent.Core.Channels.ChannelDeliveryException("DesktopUnavailable", false, true);
        icon.ShowBalloonTip(5000, "Personal Agent", DesktopOutboundChannel.Preview(text), Forms.ToolTipIcon.Info);
    }
}
