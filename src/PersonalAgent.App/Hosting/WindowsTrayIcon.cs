using System.Windows.Input;
using PersonalAgent.App.ViewModels;
using Forms = System.Windows.Forms;

namespace PersonalAgent.App;

// Only the real desktop startup creates this OS resource; headless hosts/tests never do.
public interface ITrayIcon : IDisposable
{
    void Start(ControlPanelViewModel model, Action open, Action controls, Action exit);
    void Refresh(string status);
}

public sealed class WindowsTrayIcon : ITrayIcon
{
    private Forms.NotifyIcon? icon;
    private Forms.ContextMenuStrip? menu;
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
        icon = new Forms.NotifyIcon { Icon = System.Drawing.SystemIcons.Application, Text = "Personal Agent", ContextMenuStrip = menu, Visible = true };
        icon.DoubleClick += (_, _) => open();
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
        menu?.Dispose(); menu = null; commands.Clear();
    }
}
