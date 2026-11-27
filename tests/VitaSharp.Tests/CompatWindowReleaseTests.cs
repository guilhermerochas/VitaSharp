using VitaSharp.Core.Hle.Modules;
using VitaSharp.Core.Memory;
using Xunit;
using static VitaSharp.Tests.HleHarness;

namespace VitaSharp.Tests;

/// <summary>
/// SceCompat's memory window (64 MB) goes back when its owner ends (SceCompat 3.74 0x81000488, the owner's process
/// event): the system's end (the first process disposed) must free it. Before, every test session that took the window
/// left 64 MB of user memory behind, and a suite that took it ~20 times ran out of user address space (other tests then
/// failed with "Guest User memory exhausted" or a fixed user address already taken).
/// </summary>
[Collection("guest")]
public sealed class CompatWindowReleaseTests(GuestMemoryFixture fixture)
{
    [Fact]
    public void TheWindowIsFreedWhenItsOwnerEnds()
    {
        uint window;
        var harness = new HleHarness(fixture, "compatwindow");
        try
        {
            harness.Emu.IsSystemProgram = true;
            uint bases = harness.Alloc(8);
            Assert.Equal(0, Call(harness.Module<Compat>(), "sceCompatAllocCdramWithHole", bases));
            window = GuestMemory.R32(bases);
            Assert.Contains(GuestMemory.Instance!.Blocks(), b => b.Address == window && b.Name == "SceCompat window");
        }
        finally
        {
            harness.Dispose();
        }

        Assert.DoesNotContain(GuestMemory.Instance!.Blocks(), b => b.Address == window && b.Name == "SceCompat window");
    }
}
