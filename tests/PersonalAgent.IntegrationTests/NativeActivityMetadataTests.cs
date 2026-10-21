using PersonalAgent.Computer;
using System.Runtime.InteropServices;

namespace PersonalAgent.IntegrationTests;

public sealed class NativeActivityMetadataTests
{
    [Fact]
    public void ProcessMarkerSurvives32BitMetadataRoundTripWithoutRelaxingExactComparison()
    {
        Assert.InRange(InputActivityAttribution.Marker, (nuint)1, (nuint)int.MaxValue);
        var signedRoundTrip = unchecked((nuint)(nint)(int)(uint)InputActivityAttribution.Marker);
        var unsignedRoundTrip = (nuint)(uint)InputActivityAttribution.Marker;
        foreach (var extra in new[] { signedRoundTrip, unsignedRoundTrip })
        {
            var activity = new ObservedInputActivity(ObservedInputDevice.Mouse, true, false, 0, extra);
            Assert.Equal(InputActivityOrigin.OwnInjected, InputActivityAttribution.Classify(activity));
            Assert.True(InputActivityAttribution.RequiresTakeover(activity with { Injected = false }));
            Assert.True(InputActivityAttribution.RequiresTakeover(activity with { LowerIntegrityInjected = true }));
        }
    }

    [Theory]
    [InlineData(true)] [InlineData(false)]
    public void ActualNativeMemoryLayoutRetainsInjectedFlagAndPointerSizedProcessMarker(bool keyboard)
    {
        var size = keyboard ? Marshal.SizeOf<WindowsActivityHooks.KeyboardData>() : Marshal.SizeOf<WindowsActivityHooks.MouseData>();
        var pointer = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.Copy(new byte[size], 0, pointer, size);
            Marshal.WriteInt32(pointer, keyboard ? 8 : 12, keyboard ? 0x10 : 1);
            Marshal.WriteInt32(pointer, keyboard ? 12 : 16, 42);
            Marshal.WriteIntPtr(pointer, keyboard ? 16 : IntPtr.Size == 8 ? 24 : 20, unchecked((nint)InputActivityAttribution.Marker));
            var activity = keyboard ? WindowsActivityHooks.Keyboard(Marshal.PtrToStructure<WindowsActivityHooks.KeyboardData>(pointer)) :
                WindowsActivityHooks.Mouse(Marshal.PtrToStructure<WindowsActivityHooks.MouseData>(pointer));
            Assert.True(activity.Injected); Assert.False(activity.LowerIntegrityInjected); Assert.Equal(42U, activity.Tick);
            Assert.Equal(InputActivityAttribution.Marker, activity.ExtraInfo);
            Assert.Equal(InputActivityOrigin.OwnInjected, InputActivityAttribution.Classify(activity));
            Assert.True(InputActivityAttribution.InspectMarker(activity).Exact);
            if (IntPtr.Size == 8)
            {
                var foreign = activity with { ExtraInfo = activity.ExtraInfo ^ unchecked((nuint)(1UL << 32)) };
                Assert.False(InputActivityAttribution.InspectMarker(foreign).Exact);
                Assert.True(InputActivityAttribution.InspectMarker(foreign).Lower32);
                Assert.True(InputActivityAttribution.RequiresTakeover(foreign)); // Width diagnostics cannot suppress human control.
            }
        }
        finally { Marshal.FreeHGlobal(pointer); }
    }
}
