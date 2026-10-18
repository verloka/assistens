using PersonalAgent.Core.Computer;

namespace PersonalAgent.Computer;

// Driver primitive only. The future tool supplies a fresh observed frame and trusted lease policy.
internal sealed class WindowsMouseInput
{
    private readonly WindowsInputSender sender;
    private readonly Func<IReadOnlyList<ScreenMonitor>> readMonitors;

    internal WindowsMouseInput(Func<(bool Allowed, long Generation)>? authorize = null)
        : this(authorize, new WindowsInputNative(), () => new WindowsCaptureNative().GetMonitors()) { }
    internal WindowsMouseInput(Func<(bool Allowed, long Generation)>? authorize, IWindowsInputNative native,
        Func<IReadOnlyList<ScreenMonitor>> readMonitors)
    { sender = new(authorize, native); this.readMonitors = readMonitors; }

    internal Task ClickAsync(ScreenCapture context, ScreenPoint point, MouseButton button, CancellationToken ct = default)
    {
        context.Validate();
        if (!Enum.IsDefined(button)) throw new ArgumentOutOfRangeException(nameof(button));
        // Snapshot mutable monitor collections before waiting. No screenshot bytes are logged or forwarded.
        var monitors = context.Monitors.ToArray();
        if (!context.SourceBounds.Contains(point) || !monitors.Any(monitor => monitor.Bounds.Contains(point)))
            throw Error("InvalidCoordinates");
        return sender.SendAsync(() =>
        {
            var current = readMonitors().ToArray();
            if (current.Length == 0 || current.Count(m => m.IsPrimary) != 1 || current.Select(m => m.Id).Distinct().Count() != current.Length)
                throw Error("DesktopUnavailable");
            foreach (var monitor in current) monitor.Validate();
            if (monitors.Any(monitor => !current.Contains(monitor))) throw Error("StaleDesktopContext");
            if (!current.Any(monitor => monitor.Bounds.Contains(point))) throw Error("InvalidCoordinates");
            var left = current.Min(m => m.Bounds.X);
            var top = current.Min(m => m.Bounds.Y);
            var width = current.Max(m => (long)m.Bounds.X + m.Bounds.Width) - left;
            var height = current.Max(m => (long)m.Bounds.Y + m.Bounds.Height) - top;
            // Absolute mouse coordinates have only 65536 bins per axis. Never silently click another pixel.
            if (width > 65536 || height > 65536) throw Error("UnsupportedDesktopGeometry");
            // Nearest normalized pixel center, ties toward the lower bin, including 65536-wide desktops.
            var x = (int)(((2L * ((long)point.X - left) + 1) * 65536 + width - 1) / (2 * width));
            var y = (int)(((2L * ((long)point.Y - top) + 1) * 65536 + height - 1) / (2 * height));
            var down = button switch { MouseButton.Left => 2u, MouseButton.Right => 8u, _ => 32u };
            return [NativeInput.Mouse(0xc001, x, y), NativeInput.Mouse(down), NativeInput.Mouse(down << 1)];
        }, context.DesktopGeneration, ct);
    }
    private static ComputerInputException Error(string code) => new(code, 0, 0, 0, true);
}
