using VitaSharp.Core.Hle;
using Xunit;
using static VitaSharp.Tests.HleHarness;

namespace VitaSharp.Tests;

/// <summary>
/// The FW 3.74 ScePervasiveForDriver SoC identification getters (lowio.skprx) answer the power-on misc words of a
/// retail PCH-1000 as the module copied them at start; the
/// UART baud rate setter writes the clock word of each accepted rate (0x803F0000 for another rate or port). The
/// exports are found by name so the proof without them still builds.
/// </summary>
[Collection("guest")]
public sealed class KernelPervasiveDefaultsTests(GuestMemoryFixture fixture) : IDisposable
{
    private const int InvalidArgument = unchecked((int)0x803F0000);

    private static readonly (uint Rate, uint Clock)[] s_rates =
    [
        (300, 0x12710),
        (9600, 0x10139),
        (115200, 0x1001A),
        (250000, 0x1000C),
        (460800, 0x2001A),
        (921600, 0x2000D),
        (3000000, 0x10001),
    ];

    private readonly HleHarness _harness = new(fixture, "kpdef");

    public void Dispose() => _harness.Dispose();

    private HleModule Pervasive => _harness.Emu.Hle.Modules.Single(module => module.GetType().Name == "PervasiveKernel");

    private uint Register(uint offset) => (uint)Invoke(Pervasive, "Register", offset)!;

    [Fact]
    public void TheSocRevisionIsTheRetailPch1000sAndTheOtherWordsAreZero()
    {
        uint revision = (uint)Invoke(Pervasive, "kscePervasiveGetSoCRevision")!;
        Assert.Equal(0x80000042u, revision);
        // what the FW 3.74 readers test: the first SoC (bits 8..16 zero), revision 4.2 (ScePower and msif take the
        // newer path from 0x42, SceGpuEs4 from 0x33), a retail unit without the extra RAM bank (bit 31)
        Assert.Equal(0u, revision & 0x1FF00);
        Assert.True((revision & 0xFF) >= 0x42);
        Assert.NotEqual(0u, revision & 0x80000000);
        Assert.Equal(revision, Register(0));
        Assert.Equal(0u, (uint)Invoke(Pervasive, "NID_41C3C299")!);
        Assert.Equal(0u, (uint)Invoke(Pervasive, "NID_FB0EF8C7")!);
        Assert.Equal(0u, (uint)Invoke(Pervasive, "NID_443E2A3F")!);
        Assert.Equal(0u, (uint)Invoke(Pervasive, "NID_F58EF3A5")!);
    }

    [Fact]
    public void TheGettersAnswerTheWordsAsTheModuleReadThemAtStart()
    {
        uint revision = (uint)Invoke(Pervasive, "kscePervasiveGetSoCRevision")!;
        uint misc = (uint)Invoke(Pervasive, "PageAddress", 0xE3100000u)!;
        VitaSharp.Core.Memory.GuestMemory.W32(misc, 0x12345678);
        VitaSharp.Core.Memory.GuestMemory.W32(misc + 0x304, 0x9ABC);
        Assert.Equal(revision, (uint)Invoke(Pervasive, "kscePervasiveGetSoCRevision")!);
        Assert.Equal(0u, (uint)Invoke(Pervasive, "NID_443E2A3F")!);
    }

    [Fact]
    public void TheUartBaudRateSetterWritesTheClockWordOfTheRate()
    {
        foreach ((uint rate, uint clock) in s_rates)
        {
            Assert.Equal(0, Call(Pervasive, "kscePervasiveUartSetBaudrate", 6u, rate));
            Assert.Equal(clock, Register(0x5000 + 6 * 4));
        }

        Assert.Equal(0, Call(Pervasive, "kscePervasiveUartSetBaudrate", 0u, 115200u));
        Assert.Equal(0x1001Au, Register(0x5000));
        Assert.Equal(InvalidArgument, Call(Pervasive, "kscePervasiveUartSetBaudrate", 7u, 115200u));
        Assert.Equal(InvalidArgument, Call(Pervasive, "kscePervasiveUartSetBaudrate", 0u, 115201u));
        Assert.Equal(0x1001Au, Register(0x5000));
    }
}
