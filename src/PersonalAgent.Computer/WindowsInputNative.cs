using System.Runtime.InteropServices;

namespace PersonalAgent.Computer;

[StructLayout(LayoutKind.Sequential)]
internal struct NativeInput
{
    internal uint Type;
    internal NativeInputUnion Data;

    internal static NativeInput Unicode(char value, bool up) => new()
    { Type = 1, Data = new() { Keyboard = new() { Scan = value, Flags = 4u | (up ? 2u : 0) } } };
    internal static NativeInput Key(ushort key, bool up) => new()
    { Type = 1, Data = new() { Keyboard = new() { VirtualKey = key, Flags = up ? 2u : 0 } } };
    internal static NativeInput Mouse(uint flags, int x = 0, int y = 0, uint data = 0) => new()
    { Data = new() { Mouse = new() { X = x, Y = y, Data = data, Flags = flags } } };
}
[StructLayout(LayoutKind.Explicit)]
internal struct NativeInputUnion
{
    [FieldOffset(0)] internal NativeKeyboardInput Keyboard;
    [FieldOffset(0)] internal NativeMouseInput Mouse;
}
[StructLayout(LayoutKind.Sequential)]
internal struct NativeKeyboardInput
{ internal ushort VirtualKey, Scan; internal uint Flags, Time; internal nuint Extra; }
[StructLayout(LayoutKind.Sequential)]
internal struct NativeMouseInput
{ internal int X, Y; internal uint Data, Flags, Time; internal nuint Extra; }

internal readonly record struct InputInsertion(uint Count, int NativeError);
internal interface IWindowsInputNative
{
    bool IsDesktopAvailable();
    InputInsertion Send(NativeInput[] inputs);
}
internal sealed class WindowsInputNative : IWindowsInputNative
{
    public bool IsDesktopAvailable() => new WindowsCaptureNative().IsDesktopAvailable();
    public InputInsertion Send(NativeInput[] inputs)
    {
        var count = SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<NativeInput>());
        return new(count, Marshal.GetLastPInvokeError());
    }
    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint count, [In] NativeInput[] inputs, int size);
}
