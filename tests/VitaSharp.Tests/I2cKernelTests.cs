using VitaSharp.Core.Hle;
using VitaSharp.Core.Hle.Modules;
using VitaSharp.Core.Memory;
using Xunit;
using static VitaSharp.Tests.HleHarness;

namespace VitaSharp.Tests;

/// <summary>
/// SceI2cForDriver (lowio 3.74): the bus, address and length checks in the module's order, the debug handlers around
/// a transfer, the no-device answer of the emulated buses (0x803F0380), and the kept controller settings.
/// </summary>
[Collection("guest")]
public sealed class I2cKernelTests(GuestMemoryFixture fixture) : IDisposable
{
    private const int Bus = unchecked((int)0x803F0300), Length = unchecked((int)0x803F0302);
    private const int Address = unchecked((int)0x803F0303), NoAck = unchecked((int)0x803F0380);

    private readonly HleHarness _harness = new(fixture, "i2ck");

    public void Dispose() => _harness.Dispose();

    private SystemServicesVsh Services => _harness.Module<SystemServicesVsh>();

    [Theory]
    [InlineData(0x30CF9469u)]
    [InlineData(0xE449AC6Eu)]
    [InlineData(0x9CF8F3D6u)]
    [InlineData(0x76D277ABu)]
    [InlineData(0xA2C7CE62u)]
    [InlineData(0xD1D0A9A4u)]
    [InlineData(0xCA94A759u)]
    [InlineData(0x0A40B7BFu)]
    public void EveryExportHasAnEntry(uint nid)
    {
        var bindings = HleBindings.Modules.SelectMany(m => m.Bindings())
            .Where(b => b.Nid == nid && b.Library == "SceI2cForDriver").ToList();
        Assert.Single(bindings);
        Assert.False(bindings[0].Default);
    }

    [Fact]
    public void TransfersAreCheckedInTheModulesOrder()
    {
        uint buffer = _harness.Alloc(64);
        Assert.Equal(Bus, Call(Services, "ksceI2cTransferWrite", 2u, 0x300u, buffer, 100u));
        Assert.Equal(Address, Call(Services, "ksceI2cTransferWrite", 0u, 0x100u, buffer, 100u));
        Assert.Equal(Length, Call(Services, "ksceI2cTransferWrite", 0u, 0x40u, buffer, 65u));
        Assert.Equal(NoAck, Call(Services, "ksceI2cTransferWrite", 0u, 0x40u, buffer, 0u)); // a write may be empty
        Assert.Equal(Length, Call(Services, "ksceI2cTransferRead", 1u, 0x40u, buffer, 0u));
        Assert.Equal(Length, Call(Services, "ksceI2cTransferRead", 1u, 0x40u, buffer, 65u));
        Assert.Equal(NoAck, Call(Services, "ksceI2cTransferRead", 1u, 0x40u, buffer, 64u));
        Assert.Equal(Address, Call(Services, "ksceI2cTransferWriteRead", 0u, 0x40u, buffer, 1u, 0x100u, buffer, 1u));
        Assert.Equal(Length, Call(Services, "ksceI2cTransferWriteRead", 0u, 0x40u, buffer, 1u, 0x40u, buffer, 0u));
        Assert.Equal(NoAck, Call(Services, "ksceI2cTransferWriteRead", 0u, 0x40u, buffer, 1u, 0x41u, buffer, 1u));
    }

    [Fact]
    public void TheDebugHandlersSeeEachTransfer()
    {
        uint seen = _harness.Alloc(8);
        uint handlers = _harness.Alloc(12);
        GuestMemory.W32(handlers + 4, Recorder(seen)); // before (bus, address, buffer, size): keeps the address
        GuestMemory.W32(handlers + 8, Recorder(seen + 4)); // after (bus, result): keeps the result
        Assert.Equal(Bus, Call(Services, "ksceI2cSetDebugHandlers", 2u, handlers));
        Assert.Equal(0, Call(Services, "ksceI2cSetDebugHandlers", 1u, handlers));
        uint buffer = _harness.Alloc(4);
        _harness.OnGuest(_ =>
        {
            Assert.Equal(NoAck, Call(Services, "ksceI2cTransferWrite", 1u, 0x9Au, buffer, 2u));
        });
        Assert.Equal(0x9Au, GuestMemory.R32(seen));
        Assert.Equal(unchecked((uint)NoAck), GuestMemory.R32(seen + 4));

        GuestMemory.W32(seen, 0);
        _harness.OnGuest(_ =>
        {
            Assert.Equal(NoAck, Call(Services, "ksceI2cTransferWrite", 0u, 0x9Au, buffer, 2u)); // bus 0 has none
        });
        Assert.Equal(0u, GuestMemory.R32(seen));
    }

    [Fact]
    public void TheControllerSettingsAreKept()
    {
        Assert.Equal(Bus, Call(Services, "ksceI2cInit", 2u));
        Assert.Equal(0, Call(Services, "ksceI2cInit", 0u));
        Assert.Equal(Bus, Call(Services, "I2cSetBusWord", 2u, 1u));
        Assert.Equal(0, Call(Services, "I2cSetBusWord", 1u, 0x1234u));
        Assert.Equal(0, Call(Services, "I2cSetBusSpeed", 1u, 10u, 20u));
        Assert.Equal(0, Call(Services, "ksceI2cReset", 0));
        Assert.Equal((0u, 0x1234u, 10u, 20u, false), Services.I2cKernelValues(1));
        Assert.Equal(0, Call(Services, "ksceI2cReset", 1));
        Assert.True(Services.I2cKernelValues(0).Released);
    }

    /// <summary>ARM code that stores its second argument at <paramref name="target"/> and returns.</summary>
    private uint Recorder(uint target)
    {
        uint code = _harness.Alloc(16);
        GuestMemory.W32(code, 0xE59F2004); // ldr r2, [pc, #4]  (the word at +12)
        GuestMemory.W32(code + 4, 0xE5821000); // str r1, [r2]
        GuestMemory.W32(code + 8, 0xE12FFF1E); // bx lr
        GuestMemory.W32(code + 12, target);
        return code;
    }
}
