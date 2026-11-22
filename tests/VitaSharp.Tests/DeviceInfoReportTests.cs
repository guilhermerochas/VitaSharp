using VitaSharp.Core.Hle;
using VitaSharp.Core.Hle.Modules;
using VitaSharp.Core.Memory;
using Xunit;
using static VitaSharp.Tests.HleHarness;

namespace VitaSharp.Tests;

/// <summary>
/// Every space report answers from the one source (DeviceCapacity, the served device's drive with the memory card
/// limit), like the firmware, where sceAppMgrGetDevInfo and SceCompat read the filesystem's devctl 0x3001: the
/// SceIoDevInfo block (u64 capacity, u64 free, u32 cluster size, u32 0) of sceIoDevctl, sceFiosDevctlSync and
/// sceCompatGetDevInf agrees with sceAppMgrGetDevInfo and sceAppUtilCacheGetDevInfo.
/// </summary>
[Collection("guest")]
public sealed class DeviceInfoReportTests(GuestMemoryFixture fixture) : IDisposable
{
    private readonly HleHarness _harness = new(fixture, "devinfo");

    public void Dispose() => _harness.Dispose();

    private (ulong Total, ulong Free) Served(string device)
    {
        string? host = _harness.Emu.Vfs.Resolve(device);
        Assert.NotNull(host);
        return DeviceCapacity.Of(host);
    }

    /// <summary>Runs <paramref name="report"/> until the host drive's free space is the same before and after it (other
    /// processes write to the drive meanwhile), then checks the block against that value.</summary>
    private void AssertReportsTheServedDrive(string device, Func<uint> report, Action<uint, (ulong Total, ulong Free)> check)
    {
        const int Attempts = 8;
        for (int attempt = 1; ; attempt++)
        {
            (ulong Total, ulong Free) before = Served(device);
            uint info = report();
            (ulong Total, ulong Free) after = Served(device);
            if (before == after || attempt == Attempts)
            {
                check(info, after);
                return;
            }
        }
    }

    private uint FilledBlock()
    {
        uint info = _harness.Alloc(0x20);
        GuestMemory.Fill(info, 0xAA, 0x20);
        return info;
    }

    private static void AssertBlock(uint info, (ulong Total, ulong Free) expected)
    {
        Assert.Equal(expected.Total, GuestMemory.Read<ulong>(info));
        Assert.Equal(expected.Free, GuestMemory.Read<ulong>(info + 8));
        Assert.Equal(DeviceCapacity.ClusterSize, GuestMemory.R32(info + 0x10));
        Assert.Equal(0u, GuestMemory.R32(info + 0x14));
        Assert.Equal(0xAAAAAAAAu, GuestMemory.R32(info + 0x18));
    }

    [Theory]
    [InlineData("ux0:")]
    [InlineData("ux0:/")]
    [InlineData("ur0:")]
    public void DevctlReportsTheServedDrive(string device)
    {
        AssertReportsTheServedDrive(device[..4], () =>
        {
            uint info = FilledBlock();
            Assert.Equal(0, Call(_harness.Module<IoFileMgr>(), "sceIoDevctl", device, 0x3001, 0u, 0u, info, 0x18u));
            return info;
        }, AssertBlock);
    }

    [Fact]
    public void DevctlAndAppMgrAgree()
    {
        uint info = FilledBlock();
        uint sizes = _harness.Alloc(16);
        uint dev = _harness.Str("ux0:");
        Assert.Equal(0, Call(_harness.Module<IoFileMgr>(), "sceIoDevctl", "ux0:", 0x3001, 0u, 0u, info, 0x18u));
        Assert.Equal(0, Call(_harness.Module<AppMgr>(), "sceAppMgrGetDevInfo", dev, new Ptr<ulong>(sizes), new Ptr<ulong>(sizes + 8)));
        Assert.Equal(GuestMemory.Read<ulong>(sizes), GuestMemory.Read<ulong>(info));
        Assert.Equal(GuestMemory.Read<ulong>(sizes + 8), GuestMemory.Read<ulong>(info + 8));
        Assert.True(GuestMemory.Read<ulong>(info) <= DeviceCapacity.MaxBytes);
    }

    [Fact]
    public void AShortBufferGetsOnlyItsBytes()
    {
        AssertReportsTheServedDrive("ux0:", () =>
        {
            uint info = FilledBlock();
            Assert.Equal(0, Call(_harness.Module<IoFileMgr>(), "sceIoDevctl", "ux0:", 0x3001, 0u, 0u, info, 16u));
            return info;
        }, (info, served) =>
        {
            Assert.Equal(served.Total, GuestMemory.Read<ulong>(info));
            Assert.Equal(served.Free, GuestMemory.Read<ulong>(info + 8));
            Assert.Equal(0xAAAAAAAAu, GuestMemory.R32(info + 0x10));
        });
    }

    [Fact]
    public void FiosDevctlForwardsTheSameBlock()
    {
        AssertReportsTheServedDrive("ux0:", () =>
        {
            uint info = FilledBlock();
            Assert.Equal(0, Call(_harness.Module<Fios2>(), "sceFiosDevctlSync", 0u, "ux0:", 0x3001, 0u, 0u, info, 0x18u));
            return info;
        }, AssertBlock);
    }

    [Fact]
    public void CompatReportsTheMemoryCard()
    {
        Compat compat = _harness.Module<Compat>();
        _harness.Emu.IsSystemProgram = true;
        uint bases = _harness.Alloc(8);
        Assert.Equal(0, Call(compat, "sceCompatAllocCdramWithHole", bases));
        AssertReportsTheServedDrive("ux0:", () =>
        {
            uint info = FilledBlock();
            Assert.Equal(0, Call(compat, "sceCompatGetDevInf", info));
            return info;
        }, AssertBlock);
    }
}
