using VitaSharp.Core.Hle;
using VitaSharp.Core.Hle.Modules;
using VitaSharp.Core.Memory;
using Xunit;
using static VitaSharp.Tests.HleHarness;

namespace VitaSharp.Tests;

/// <summary>
/// SceSdifForDriver (sdif.skprx 3.74): three hosts (eMMC, game card, SDIO) with their contexts in kernel memory; the
/// eMMC and the WLAN chip initialized, the game card inserted only while its dump is served (gro0:); bus commands
/// fail after the argument checks (0x8032000E) as an aborted command (0x8032001A); host indexes above 2 0x80320001.
/// </summary>
[Collection("guest")]
public sealed class SdifKernelTests(GuestMemoryFixture fixture) : IDisposable
{
    private const int Host = unchecked((int)0x80320001), Invalid = unchecked((int)0x8032000E);
    private const int Aborted = unchecked((int)0x8032001A), NoContext = unchecked((int)0x80320013);

    private readonly HleHarness _harness = new(fixture, "sdifk");

    public void Dispose() => _harness.Dispose();

    private SystemServicesVsh Services => _harness.Module<SystemServicesVsh>();

    private uint Pointer(string name, params object?[] args) => (uint)Invoke(Services, name, args)!;

    [Fact]
    public void EveryExportHasAnEntry()
    {
        var bindings = HleBindings.Modules.SelectMany(m => m.Bindings()).Where(b => b.Library == "SceSdifForDriver").ToList();
        Assert.Equal(59, bindings.Count);
        Assert.Equal(59, bindings.Select(b => b.Nid).Distinct().Count());
    }

    [Fact]
    public void TheHostsHaveTheirContextsAndDeviceTypes()
    {
        uint mmc = Pointer("ksceSdifGetSdContextGlobal", 0u);
        uint sd = Pointer("ksceSdifGetSdContextGlobal", 1u);
        uint sdio = Pointer("ksceSdifGetSdContextGlobal", 2u);
        Assert.NotEqual(0u, mmc);
        Assert.Equal(0x24C0u, sd - mmc);
        Assert.Equal(0u, Pointer("ksceSdifGetSdContextGlobal", 3u));
        Assert.Equal(1u, GuestMemory.R32(mmc + 0x2410));
        Assert.Equal(2u, GuestMemory.R32(sd + 0x2410));
        Assert.Equal(3u, GuestMemory.R32(sdio + 0x2410));
        uint mmcDevice = Pointer("ksceSdifGetSdContextPartValidateMmc");
        Assert.NotEqual(0u, mmcDevice);
        Assert.Equal(mmc, GuestMemory.R32(mmcDevice));
        Assert.Equal(mmcDevice, GuestMemory.R32(mmc + 0x2414));
        Assert.Equal(0x200, GuestMemory.Read<ushort>(mmcDevice + 8));
        Assert.NotEqual(0u, Pointer("ksceSdifGetSdContextPartValidateSdio"));
        uint info = _harness.Alloc(0x10);
        Assert.Equal(0, Call(Services, "ksceSdif_3DF7E207", 0u, info));
        Assert.Equal(mmcDevice, GuestMemory.R32(info));
        Assert.Equal(48_000_000u, GuestMemory.R32(info + 8));
        Assert.Equal(8u, GuestMemory.R32(info + 0xC));
        Assert.Equal(Host, Call(Services, "ksceSdif_3DF7E207", 3u, info));
    }

    [Fact]
    public void TheGameCardIsInsertedOnlyWhileItsDumpIsServed()
    {
        uint context = _harness.Alloc(4);
        if (_harness.Emu.Vfs.IsMounted("gro0"))
        {
            _harness.Emu.Vfs.Unmount("gro0");
        }

        Assert.Equal(0, Call(Services, "ksceSdifGetCardInsertState1", 1u));
        Assert.Equal(1, Call(Services, "ksceSdifGetCardInsertState2", 0u));
        Assert.Equal(NoContext, Call(Services, "ksceSdifGetCardInsertState1", 3u));
        Assert.Equal(0u, Pointer("ksceSdifGetSdContextPartValidateSd"));
        Assert.Equal(Aborted, Call(Services, "ksceSdifInitializeSdDevice", 1u, context));
        Assert.Equal(0u, GuestMemory.R32(context));

        _harness.Emu.Vfs.Mount("gro0", Path.Combine(_harness.Root, "gro0"));
        Assert.Equal(1, Call(Services, "ksceSdifGetCardInsertState1", 1u));
        Assert.Equal(0, Call(Services, "ksceSdifInitializeSdDevice", 1u, context));
        Assert.Equal(Pointer("ksceSdifGetSdContextPartValidateSd"), GuestMemory.R32(context));
        Assert.Equal(Aborted, Call(Services, "ksceSdifInitializeSdDevice", 0u, context));
        Assert.Equal(0, Call(Services, "ksceSdifInitializeMmcDevice", 0u, context));
        Assert.Equal(Host, Call(Services, "ksceSdifInitializeMmcDevice", 5u, context));
        _harness.Emu.Vfs.Unmount("gro0");
    }

    [Fact]
    public void BusCommandsAreAbortedAfterTheirChecks()
    {
        uint device = Pointer("ksceSdifGetSdContextPartValidateMmc");
        uint buffer = _harness.Alloc(0x400);
        Assert.Equal(Invalid, Call(Services, "ksceSdifReadSectorMmc", 0u, 0u, buffer, 1u));
        Assert.Equal(Invalid, Call(Services, "ksceSdifReadSectorMmc", device, 0u, 0u, 1u));
        Assert.Equal(Invalid, Call(Services, "ksceSdifWriteSectorMmc", device, 0u, buffer, 0u));
        Assert.Equal(Aborted, Call(Services, "ksceSdifReadSectorMmc", device, 0u, buffer, 1u));
        Assert.Equal(Aborted, Call(Services, "ksceSdifWriteSectorSd", device, 0u, buffer, 1u));
        Assert.Equal(Invalid, Call(Services, "ksceSdifReadCmd56", device, buffer, 0x100u));
        Assert.Equal(Aborted, Call(Services, "ksceSdifWriteCmd56", device, buffer, 0x200u));
        Assert.Equal(Aborted, Call(Services, "ksceSdif_B32776C7", device, 0x200u));
        Assert.Equal(Invalid, Call(Services, "ksceSdif_3C4CDC8B", 0u, 0u, buffer, 4u, 0u));
        Assert.Equal(Aborted, Call(Services, "ksceSdif_3C4CDC8B", device, 0u, buffer, 4u, 0u));
        Assert.Equal(Invalid, Call(Services, "ksceSdifGetCID", device, 0u));
        Assert.Equal(0, Call(Services, "ksceSdifGetCID", device, buffer));
        uint result = _harness.Alloc(4);
        Assert.Equal(0, Call(Services, "ksceSdif_E091BA2E", device, 0u, buffer, result));
        Assert.Equal(1u, GuestMemory.R32(result));
        Assert.Equal(unchecked((int)0x8032000F), Call(Services, "ksceSdif_235AD556"));
        Assert.Equal(Host, Call(Services, "ksceSdif_475D8E45", 3u));
        Assert.Equal(0, Call(Services, "ksceSdif_475D8E45", 2u));
    }

    [Fact]
    public void SlowModeIsKept()
    {
        Assert.Equal(0, Call(Services, "ksceSdifMemoryCardEnableSlowMode"));
        Assert.True(Services.SdifSlowMode);
        Assert.Equal(0, Call(Services, "ksceSdifMemoryCardDisableSlowMode"));
        Assert.False(Services.SdifSlowMode);
    }

    /// <summary>
    /// An image file backing the eMMC (VITASHARP_EMMC_IMAGE / HostServices.EmmcImage): the eMMC's sector commands read
    /// and write it (a sector written is read back and is in the file); a range past its end is the card's
    /// ADDRESS_OUT_OF_RANGE as FW 3.74 sdif reports it (0x8032011F); the argument checks come first; the game card
    /// keeps no device; every access is counted for the per-caller log. Before, every sector command was aborted.
    /// </summary>
    [Fact]
    public void AnImageBacksTheEmmcSectors()
    {
        const int Sectors = 64;
        string path = Path.Combine(_harness.Root, "emmc.img");
        byte[] contents = new byte[Sectors * 0x200];
        for (int i = 0; i < contents.Length; i++)
        {
            contents[i] = (byte)(i / 0x200);
        }

        File.WriteAllBytes(path, contents);
        _harness.Emu.Host.EmmcImage = VitaSharp.Core.Host.BlockImage.Open(path);
        uint device = Pointer("ksceSdifGetSdContextPartValidateMmc");
        uint buffer = _harness.Alloc(0x400);
        int before = Services.SectorAccesses;

        Assert.Equal(0, Call(Services, "ksceSdifReadSectorMmc", device, 5u, buffer, 2u));
        Assert.Equal((byte)5, GuestMemory.Read<byte>(buffer));
        Assert.Equal((byte)6, GuestMemory.Read<byte>(buffer + 0x3FF));

        GuestMemory.Fill(buffer, 0xA5, 0x400);
        Assert.Equal(0, Call(Services, "ksceSdifWriteSectorMmc", device, 10u, buffer, 2u));
        GuestMemory.Fill(buffer, 0, 0x400);
        Assert.Equal(0, Call(Services, "ksceSdifReadSectorMmc", device, 10u, buffer, 2u));
        Assert.Equal((byte)0xA5, GuestMemory.Read<byte>(buffer + 0x3FF));

        Assert.Equal(SystemServicesVsh.SDIF_ERR_OUT_OF_RANGE, Call(Services, "ksceSdifReadSectorMmc", device, 63u, buffer, 2u));
        Assert.Equal(SystemServicesVsh.SDIF_ERR_OUT_OF_RANGE, Call(Services, "ksceSdifWriteSectorMmc", device, 64u, buffer, 1u));
        Assert.Equal(0, Call(Services, "ksceSdifReadSectorMmc", device, 63u, buffer, 1u));
        Assert.Equal(Invalid, Call(Services, "ksceSdifReadSectorMmc", device, 0u, 0u, 1u));
        Assert.Equal(Aborted, Call(Services, "ksceSdifReadSectorSd", device, 0u, buffer, 1u));   // only the eMMC has an image
        Assert.Equal(8, Services.SectorAccesses - before);

        _harness.Emu.Host.EmmcImage.Dispose();
        _harness.Emu.Host.EmmcImage = null;
        byte[] onDisk = File.ReadAllBytes(path);
        Assert.Equal(0xA5, onDisk[10 * 0x200]);
        Assert.Equal(0xA5, onDisk[12 * 0x200 - 1]);
        Assert.Equal(12, onDisk[12 * 0x200]);
        Assert.Equal(Aborted, Call(Services, "ksceSdifReadSectorMmc", device, 0u, buffer, 1u));   // no image: no device
    }
}
