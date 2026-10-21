using System.Security.Cryptography;

namespace PersonalAgent.Computer;

public enum InputActivityOrigin { Uncertain, PhysicalCandidate, OwnInjected }

// Attribution is a safety heuristic, never authentication or permission.
public static class InputActivityAttribution
{
    internal static readonly nuint Marker = CreateMarker();
    public static InputActivityOrigin Classify(ObservedInputActivity activity)
    {
        if (!Enum.IsDefined(activity.Device) || activity.LowerIntegrityInjected) return InputActivityOrigin.Uncertain;
        if (activity.Injected && activity.ExtraInfo == Marker) return InputActivityOrigin.OwnInjected;
        if (!activity.Injected && activity.ExtraInfo == 0) return InputActivityOrigin.PhysicalCandidate;
        return InputActivityOrigin.Uncertain;
    }
    public static bool RequiresTakeover(ObservedInputActivity activity) => Classify(activity) != InputActivityOrigin.OwnInjected;

    // Stamp only a private copy after the ordinary batch validation; caller-supplied metadata stays denied.
    internal static NativeInput[] Stamp(NativeInput[] inputs)
    {
        var stamped = inputs.ToArray();
        for (var i = 0; i < stamped.Length; i++)
            if (stamped[i].Type == 1) stamped[i].Data.Keyboard.Extra = Marker;
            else if (stamped[i].Type == 0) stamped[i].Data.Mouse.Extra = Marker;
            else throw new ArgumentException("Unsupported input type.");
        return stamped;
    }
    private static nuint CreateMarker()
    {
        Span<byte> bytes = stackalloc byte[IntPtr.Size];
        nuint marker;
        do { RandomNumberGenerator.Fill(bytes); marker = IntPtr.Size == 8 ? (nuint)BitConverter.ToUInt64(bytes) : BitConverter.ToUInt32(bytes); } while (marker == 0);
        return marker;
    }
}
