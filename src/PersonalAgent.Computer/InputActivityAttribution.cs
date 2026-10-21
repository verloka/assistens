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
    // Diagnostic comparison only: the lower-width match never authorizes or suppresses takeover.
    public static (bool Exact, bool Lower32) InspectMarker(ObservedInputActivity activity) =>
        (activity.ExtraInfo == Marker, (uint)activity.ExtraInfo == (uint)Marker);

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
        // Some Windows input paths preserve only 32 bits of ExtraInfo. A positive 31-bit
        // value survives both zero/sign extension; classification still compares the entire
        // native-width field exactly. This heuristic is never an authorization credential.
        Span<byte> bytes = stackalloc byte[sizeof(uint)];
        uint marker;
        do { RandomNumberGenerator.Fill(bytes); marker = BitConverter.ToUInt32(bytes) & 0x7fff_ffffu; } while (marker == 0);
        return marker;
    }
}
