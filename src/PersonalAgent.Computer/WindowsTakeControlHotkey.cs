using System.Runtime.InteropServices;
using System.Windows.Interop;

namespace PersonalAgent.Computer;

public sealed record TakeControlHotkeyGesture(uint Modifiers = 6, uint VirtualKey = 0x79)
{
    public void Validate()
    {
        if (Modifiers is < 1 or > 7 || VirtualKey is < 1 or > 254 || VirtualKey == 0x7b)
            throw new ArgumentException("Use Ctrl/Alt/Shift plus a supported non-F12 virtual key.");
    }
}

// Message-only HWND on the caller's WPF dispatcher. Registration never activates a visible window.
public sealed class WindowsTakeControlHotkey : IDisposable
{
    internal const int HotkeyId = 0x4a01;
    private readonly Action takeControl;
    private readonly TakeControlHotkeyGesture gesture;
    private readonly IHotkeyNative native;
    private readonly HwndSource source;
    private bool disposed;
    public Exception? CallbackError { get; private set; }
    public int UnregisterError { get; private set; }

    public WindowsTakeControlHotkey(Action takeControl, TakeControlHotkeyGesture? gesture = null)
        : this(takeControl, gesture ?? new(), new HotkeyNative()) { }
    internal WindowsTakeControlHotkey(Action takeControl, TakeControlHotkeyGesture gesture, IHotkeyNative native)
    {
        gesture.Validate();
        ArgumentNullException.ThrowIfNull(takeControl);
        this.takeControl = takeControl; this.gesture = gesture; this.native = native;
        source = new HwndSource(new HwndSourceParameters("PersonalAgent.TakeControl")
        { ParentWindow = new nint(-3), WindowStyle = 0, Width = 0, Height = 0 });
        source.AddHook(OnMessage);
        if (!native.Register(source.Handle, HotkeyId, gesture.Modifiers | 0x4000, gesture.VirtualKey))
        {
            var error = native.LastError;
            source.RemoveHook(OnMessage); source.Dispose(); disposed = true;
            throw new TakeControlHotkeyException(error);
        }
    }

    private nint OnMessage(nint hwnd, int message, nint wParam, nint lParam, ref bool handled)
    { ProcessMessage(message, wParam, lParam, ref handled); return 0; }

    internal void ProcessMessage(int message, nint wParam, nint lParam, ref bool handled)
    {
        if (disposed || message != 0x312 || wParam != HotkeyId ||
            ((ulong)lParam.ToInt64() & 0xffff) != gesture.Modifiers ||
            (((ulong)lParam.ToInt64() >> 16) & 0xffff) != gesture.VirtualKey) return;
        handled = true;
        try { takeControl(); }
        catch (Exception error) { CallbackError = error; } // Never unwind a native window procedure.
    }

    public void Dispose()
    {
        if (disposed) return;
        source.Dispatcher.VerifyAccess();
        disposed = true;
        try { if (!native.Unregister(source.Handle, HotkeyId)) UnregisterError = native.LastError; }
        finally { source.RemoveHook(OnMessage); source.Dispose(); }
    }
}

public sealed class TakeControlHotkeyException(int nativeError)
    : Exception($"Take Control hotkey could not be registered (Windows error {nativeError}).")
{ public int NativeError { get; } = nativeError; }

internal interface IHotkeyNative
{
    bool Register(nint window, int id, uint modifiers, uint key);
    bool Unregister(nint window, int id);
    int LastError { get; }
}
internal sealed class HotkeyNative : IHotkeyNative
{
    public int LastError { get; private set; }
    public bool Register(nint window, int id, uint modifiers, uint key)
    { var result = RegisterHotKey(window, id, modifiers, key); LastError = Marshal.GetLastPInvokeError(); return result; }
    public bool Unregister(nint window, int id)
    { var result = UnregisterHotKey(window, id); LastError = Marshal.GetLastPInvokeError(); return result; }
    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RegisterHotKey(nint window, int id, uint modifiers, uint key);
    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnregisterHotKey(nint window, int id);
}
