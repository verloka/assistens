using PersonalAgent.Core.Computer;
using System.Runtime.InteropServices;

namespace PersonalAgent.Computer;

public sealed class WindowsSystemIdleSource : ISystemIdleSource
{
    private readonly IWindowsIdleNative native;
    private readonly object sync = new();
    private uint? lastTick;
    private ulong? observedChangeAt;
    public WindowsSystemIdleSource() : this(new WindowsIdleNative()) { }
    internal WindowsSystemIdleSource(IWindowsIdleNative native) => this.native = native;
    public SystemIdleSnapshot Read()
    {
        lock (sync)
        {
            try
            {
                if (!native.IsDesktopAvailable()) return new(false, null);
                if (!native.TryGetLastInputTick(out var input)) return new(true, null);
                var now = native.GetTickCount64();
                if (lastTick is { } previous && previous != input) observedChangeAt = now;
                lastTick = input;
                // LASTINPUTINFO is 32 bit; unsigned subtraction handles an ordinary tick wrap.
                var elapsed = (ulong)unchecked((uint)now - input);
                if (elapsed > int.MaxValue || elapsed > now || observedChangeAt > now) return new(true, null);
                if (observedChangeAt is { } changed) elapsed = Math.Min(elapsed, now - changed);
                return new(true, TimeSpan.FromMilliseconds(elapsed));
            }
            catch (Exception) { return new(false, null); }
        }
    }
}

internal interface IWindowsIdleNative
{
    bool IsDesktopAvailable();
    bool TryGetLastInputTick(out uint tick);
    ulong GetTickCount64();
}

internal sealed class WindowsIdleNative : IWindowsIdleNative
{
    private readonly WindowsCaptureNative desktop = new();
    public bool IsDesktopAvailable() => desktop.IsDesktopAvailable(); // Read-only active/default desktop check; never captures.
    public bool TryGetLastInputTick(out uint tick)
    {
        var info = new LastInputInfo { Size = (uint)Marshal.SizeOf<LastInputInfo>() };
        var success = GetLastInputInfo(ref info); tick = info.Time; return success;
    }
    public ulong GetTickCount64() => ReadTickCount64();
    // https://learn.microsoft.com/windows/win32/api/winuser/nf-winuser-getlastinputinfo
    // Session-specific; supplied SendInput timestamps are not necessarily incremental.
    [StructLayout(LayoutKind.Sequential)] private struct LastInputInfo { public uint Size; public uint Time; }
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetLastInputInfo(ref LastInputInfo info);
    [DllImport("kernel32.dll", EntryPoint = "GetTickCount64")] private static extern ulong ReadTickCount64();
}
