using VitaSharp.Core.Hle;
using VitaSharp.Core.Hle.Modules;
using VitaSharp.Core.Memory;
using Xunit;
using static VitaSharp.Tests.HleHarness;

namespace VitaSharp.Tests;

/// <summary>
/// SceMsifForDriver (msif.skprx 3.74): the memory card is inserted (probe, media check, access mode, the information
/// block with the served ux0: capacity); raw sector access and the card's authentication fail after the argument checks
/// (0x803D0001, an unaligned buffer 0x803D0002) as a failed card command (0x803D0000); the caller's flag kept.
/// </summary>
[Collection("guest")]
public sealed class MsifKernelTests(GuestMemoryFixture fixture) : IDisposable
{
    private const int Invalid = unchecked((int)0x803D0001), Alignment = unchecked((int)0x803D0002);
    private const int Device = unchecked((int)0x803D0000);

    private readonly HleHarness _harness = new(fixture, "msifk");

    public void Dispose() => _harness.Dispose();

    private SystemServicesVsh Services => _harness.Module<SystemServicesVsh>();

    [Theory]
    [InlineData(0x0F86BEEAu)]
    [InlineData(0x329035EFu)]
    [InlineData(0x491E25B5u)]
    [InlineData(0x4B751CE6u)]
    [InlineData(0x4EA579EFu)]
    [InlineData(0x58654AA3u)]
    [InlineData(0x6EDE7DBAu)]
    [InlineData(0x718BDFDEu)]
    [InlineData(0x75848756u)]
    [InlineData(0x855E1E38u)]
    [InlineData(0xD0307849u)]
    [InlineData(0xF997286Bu)]
    public void EveryExportHasAnEntry(uint nid)
    {
        var bindings = HleBindings.Modules.SelectMany(m => m.Bindings())
            .Where(b => b.Nid == nid && b.Library == "SceMsifForDriver").ToList();
        Assert.Single(bindings);
    }

    [Fact]
    public void TheCardIsInsertedAndRawBlockAccessFails()
    {
        uint buffer = _harness.Alloc(0x400);
        Assert.Equal(Invalid, Call(Services, "ksceMsifReadSector", 0u, 0u, 1u));
        Assert.Equal(Invalid, Call(Services, "ksceMsifReadSector", 0u, buffer, 0u));
        Assert.Equal(Alignment, Call(Services, "ksceMsifReadSector", 0u, buffer + 2, 1u));
        Assert.Equal(Device, Call(Services, "ksceMsifReadSector", 0u, buffer, 1u));
        Assert.Equal(Alignment, Call(Services, "ksceMsifWriteSector", 0u, buffer + 1, 1u));
        Assert.Equal(Device, Call(Services, "ksceMsifWriteSector", 0u, buffer, 2u));
        Assert.Equal(Invalid, Call(Services, "ksceMsifEraseSector", 0u, 0u));
        Assert.Equal(Device, Call(Services, "ksceMsifEraseSector", 0u, 8u));
        Assert.Equal(Device, Call(Services, "ksceMsifAuthenticate", buffer));
        Assert.Equal(Invalid, Call(Services, "ksceMsifSetAccessMode", 2u));
        Assert.Equal(0, Call(Services, "ksceMsifSetAccessMode", 1u));
        Assert.Equal(0, Call(Services, "ksceMsifInit"));
        Assert.Equal(0, Call(Services, "ksceMsifCheckMedia"));
    }

    [Fact]
    public void TheInformationBlockReportsTheServedMemoryCard()
    {
        uint info = _harness.Alloc(0x40);
        GuestMemory.Fill(info, 0xAA, 0x40);
        Assert.Equal(Invalid, Call(Services, "ksceMsifGetMediaInfo", 0u));
        Assert.Equal(0, Call(Services, "ksceMsifGetMediaInfo", info));
        string? host = _harness.Emu.Vfs.Resolve("ux0:");
        ulong expected = host == null ? DeviceCapacity.MaxBytes : DeviceCapacity.Of(host).Total & ~0x1FFUL;
        Assert.Equal(2u, GuestMemory.R32(info));
        Assert.Equal(expected, GuestMemory.Read<ulong>(info + 8));
        Assert.Equal(expected, GuestMemory.Read<ulong>(info + 0x10));
        Assert.Equal(0x200u, GuestMemory.R32(info + 0x18));
        Assert.Equal(0x8000u, GuestMemory.R32(info + 0x20));
        Assert.Equal(0u, GuestMemory.R32(info + 0x38));
        Assert.Equal(0xAAAAAAAAu, GuestMemory.R32(info + 0x3C));
        Assert.True(expected > 0 && expected <= DeviceCapacity.MaxBytes);
    }

    [Fact]
    public void TheFlagIsKeptAndNoTransferTookTime()
    {
        Assert.Equal(0, Call(Services, "ksceMsifGetFlag"));
        Assert.Equal(0, Call(Services, "ksceMsifSetFlag"));
        Assert.Equal(1, Call(Services, "ksceMsifGetFlag"));
        Assert.Equal(0, Call(Services, "ksceMsifClearFlag"));
        Assert.Equal(0, Call(Services, "ksceMsifGetFlag"));
        Assert.Equal(0UL, (ulong)Invoke(Services, "ksceMsifGetLastTransferTime")!);
    }
}
