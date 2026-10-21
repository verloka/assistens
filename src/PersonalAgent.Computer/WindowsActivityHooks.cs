using System.Collections.Concurrent;
using System.Runtime.InteropServices;

namespace PersonalAgent.Computer;

internal sealed class WindowsActivityHooks : IWindowsActivityHooks
{
    // A failed native unhook must never leave a collectible delegate behind a native function pointer.
    // No automatic restart/reinstall is performed; retain these roots until process exit on this exceptional path.
    private static readonly ConcurrentBag<HookProc> failedCleanupRoots = [];
    public IDisposable Install(Action<ObservedInputActivity> observe) => new Registration(observe);

    private sealed class Registration : IDisposable
    {
        private readonly HookProc keyboard, mouse;
        private nint keyboardHook, mouseHook;
        private bool disposed;
        public Registration(Action<ObservedInputActivity> observe)
        {
            keyboard = (code, message, data) => Process(code, message, data, true, observe);
            mouse = (code, message, data) => Process(code, message, data, false, observe);
            try
            {
                var module = GetModuleHandle(null);
                keyboardHook = SetWindowsHookEx(13, keyboard, module, 0); // WH_KEYBOARD_LL
                if (keyboardHook == 0) throw new InvalidOperationException("InputObservationUnavailable");
                mouseHook = SetWindowsHookEx(14, mouse, module, 0); // WH_MOUSE_LL
                if (mouseHook == 0) throw new InvalidOperationException("InputObservationUnavailable");
            }
            catch { Dispose(); throw; }
        }
        private nint Process(int code, nuint message, nint data, bool isKeyboard, Action<ObservedInputActivity> observe)
        {
            try
            {
                if (!disposed && code == 0 && data != 0 && IsInputMessage(isKeyboard, message))
                {
                    var activity = isKeyboard ? Keyboard(Marshal.PtrToStructure<KeyboardData>(data)) : Mouse(Marshal.PtrToStructure<MouseData>(data));
                    observe(activity);
                }
            }
            catch (Exception) { /* Never suppress input or unwind through user32, even on a consumer failure. */ }
            return CallNextHookEx(0, code, message, data);
        }
        public void Dispose()
        {
            if (disposed) return; disposed = true;
            var failed = false;
            if (mouseHook != 0 && !UnhookWindowsHookEx(mouseHook)) { failedCleanupRoots.Add(mouse); failed = true; }
            if (keyboardHook != 0 && !UnhookWindowsHookEx(keyboardHook)) { failedCleanupRoots.Add(keyboard); failed = true; }
            mouseHook = keyboardHook = 0;
            if (failed) throw new InvalidOperationException("InputObservationCleanupFailed");
        }
    }
    internal static bool IsInputMessage(bool keyboard, nuint message) => keyboard ? message is 0x100 or 0x101 or 0x104 or 0x105 :
        message is 0x200 or 0x201 or 0x202 or 0x204 or 0x205 or 0x207 or 0x208 or 0x20a or 0x20b or 0x20c or 0x20e;
    internal static ObservedInputActivity Keyboard(KeyboardData data) => new(ObservedInputDevice.Keyboard, (data.Flags & 0x10) != 0, (data.Flags & 2) != 0, data.Time, data.Extra);
    internal static ObservedInputActivity Mouse(MouseData data) => new(ObservedInputDevice.Mouse, (data.Flags & 1) != 0, (data.Flags & 2) != 0, data.Time, data.Extra);
    [StructLayout(LayoutKind.Sequential)] internal struct KeyboardData { internal uint Key, Scan, Flags, Time; internal nuint Extra; }
    [StructLayout(LayoutKind.Sequential)] internal struct MouseData { internal int X, Y; internal uint Data, Flags, Time; internal nuint Extra; }
    private delegate nint HookProc(int code, nuint message, nint data);
    [DllImport("user32.dll", EntryPoint = "SetWindowsHookExW", SetLastError = true)]
    private static extern nint SetWindowsHookEx(int id, HookProc callback, nint module, uint thread);
    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool UnhookWindowsHookEx(nint hook);
    [DllImport("user32.dll")] private static extern nint CallNextHookEx(nint hook, int code, nuint message, nint data);
    [DllImport("kernel32.dll", EntryPoint = "GetModuleHandleW", CharSet = CharSet.Unicode)] private static extern nint GetModuleHandle(string? module);
}
