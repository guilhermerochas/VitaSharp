using System.Reflection;
using VitaSharp.Core.Hle.Modules;
using VitaSharp.Core.Memory;

namespace VitaSharp.Tests;

/// <summary>SceSystemGesture touch recognizers.</summary>
[Collection("guest")]
public sealed class SystemGestureHleTests(GuestMemoryFixture fx)
{
    const int INVALID_ARG = unchecked((int)0x80800001);

    static int Call(SystemGesture g, string name, params object[] args) =>
        Convert.ToInt32(typeof(SystemGesture).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)!.Invoke(g, args));

    uint Alloc(uint size) { uint p = fx.Memory.Alloc(size, "gesture"); GuestMemory.Fill(p, 0, size); return p; }

    [Fact]
    public void EveryRecognizerTypeCanBeCreated()
    {
        // PhyreEngine titles (Blast 'Em Bunnies, FFX HD) create tap, drag, tap-and-hold, pinch and rotation (0x10)
        // recognizers and shut down during init when any create fails
        var g = new SystemGesture();
        uint rect = Alloc(8);
        GuestMemory.Write(rect + 4, (short)1919); GuestMemory.Write(rect + 6, (short)1087);
        foreach (uint type in new uint[] { 1, 2, 4, 8, 0x10 })
            Assert.Equal(0, Call(g, "sceSystemGestureCreateTouchRecognizer", Alloc(0x998), type, 0u, rect, 0u));
        Assert.Equal(INVALID_ARG, Call(g, "sceSystemGestureCreateTouchRecognizer", Alloc(0x998), 0x20u, 0u, rect, 0u));
        Assert.Equal(INVALID_ARG, Call(g, "sceSystemGestureCreateTouchRecognizer", Alloc(0x998), 3u, 0u, rect, 0u));
        Assert.Equal(INVALID_ARG, Call(g, "sceSystemGestureCreateTouchRecognizer", 0u, 1u, 0u, rect, 0u));
    }

    [Fact]
    public void RotationAngleWrapsToHalfTurn()
    {
        Assert.Equal(MathF.PI / 2, SystemGesture.RotationSince(0, MathF.PI / 2), 5);
        // from just below +pi to just above -pi is a small positive turn, not almost a full negative one
        Assert.Equal(0.2f, SystemGesture.RotationSince(MathF.PI - 0.1f, -MathF.PI + 0.1f), 4);
        Assert.Equal(-0.2f, SystemGesture.RotationSince(-MathF.PI + 0.1f, MathF.PI - 0.1f), 4);
    }
}
