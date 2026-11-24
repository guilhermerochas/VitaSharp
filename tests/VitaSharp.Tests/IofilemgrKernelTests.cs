using VitaSharp.Core.Hle;
using VitaSharp.Core.Hle.Modules;
using VitaSharp.Core.Memory;
using Xunit;
using static VitaSharp.Tests.HleHarness;

namespace VitaSharp.Tests;

/// <summary>
/// SceIofilemgrForDriver (iofilemgr.skprx 3.74) beyond the twins: the VFS layer (driver registration, vnodes, files,
/// locks, the vnode and VFS operations), the PFS mounts (ScePfsMgrForKernel) over the served folders, the facade
/// stand-ins (-1), and the kernel services on the same open files (fd lists, file info, media types, events).
/// </summary>
[Collection("guest")]
public sealed class IofilemgrKernelTests(GuestMemoryFixture fixture) : IDisposable
{
    private const int Perm = unchecked((int)0x80010001), NoEnt = unchecked((int)0x80010002);
    private const int Busy = unchecked((int)0x80010010), Exist = unchecked((int)0x80010011);
    private const int NotMounted = unchecked((int)0x80010013), Inval = unchecked((int)0x80010016);
    private const int Unsup = unchecked((int)0x80010030);

    private static readonly uint[] s_driverNids =
    [
        0x03F6A684u, 0x12F8D58Eu, 0x15C17487u, 0x2EFDFA12u, 0x3675ECB9u, 0x39ABDB9Eu, 0x3A79FAC9u, 0x5B7E5AB8u,
        0x6BEEDDB4u, 0x8F0DE34Du, 0x9FCDCE62u, 0xA39A9CA7u, 0xAC0E9AAAu, 0xB499287Eu, 0xC722DF35u, 0xCCE94599u,
        0xDD46CD63u, 0xF4F8C59Au, 0x40B933C7u, 0x3C0343DBu, 0x2DFA192Fu, 0xC6158F8Du, 0x43DB0AE4u, 0xA1EF2648u,
        0xDCF75F6Du, 0x9C220246u, 0x0D493274u, 0xCE397158u, 0x44EDCE57u, 0xD070BC48u, 0xABE65071u, 0x20574100u,
        0x673D2FCDu, 0x9CBFA725u, 0xF7DAC0F5u, 0xB07B307Du, 0x1974FA92u, 0x0F7E1718u, 0x40944C2Eu, 0x9E347C7Du,
        0x1350F5C7u, 0x00C9C2DDu, 0x77584C8Fu, 0x082AFD7Fu, 0x1DBCBB01u, 0x50A63ACFu, 0x8FB94521u, 0x333C904Du,
        0x942AA61Fu, 0xA5A6A55Cu, 0xB2B13818u, 0x2F3F8C70u, 0x76B79BECu, 0xABBC80E3u, 0xA53C040Du, 0x570388A5u,
        0xDC1E7EE4u, 0x36A794C7u, 0x1D551105u, 0x9CD96406u, 0x0D8A806Eu, 0xF53399BCu, 0x9A68378Du, 0xEEAE8B51u,
        0xBBAC1751u, 0xCDF3EF52u, 0xA7020B0Du, 0xD6AB5E4Bu, 0xBCE4865Bu, 0x7517FE29u, 0x1AE14011u, 0xC85E33A2u,
        0x21D57633u, 0xB987450Du, 0xC20C621Cu, 0xD60B5C63u, 0xAA253B68u, 0xC1DD4317u, 0xC468B5EFu, 0xC3AE93A2u,
        0x6B3CA9F7u, 0xAA45010Bu, 0xB62DE9A6u, 0xFEEE44A9u, 0x0E518FA9u, 0x0B54F9E0u, 0xE5DEA6B7u, 0x809892C1u,
        0x69A7E076u, 0x9C7E7B76u, 0xD220539Du, 0xDC2D8BCEu, 0x6048F245u, 0x8598ADC3u,
    ];

    private readonly HleHarness _harness = new(fixture, "iofilemgrk");

    public void Dispose() => _harness.Dispose();

    private IoFileMgr Io => _harness.Module<IoFileMgr>();

    private static int Bound(string library, uint nid) => HleBindings.Modules.SelectMany(m => m.Bindings())
        .Count(b => b.Nid == nid && b.Library == library);

    [Fact]
    public void EveryExportHasAnEntry()
    {
        Assert.Equal(94, s_driverNids.Distinct().Count());
        Assert.All(s_driverNids, nid => Assert.Equal(1, Bound("SceIofilemgrForDriver", nid)));
        var pfs = HleBindings.Modules.SelectMany(m => m.Bindings()).Where(b => b.Library == "ScePfsMgrForKernel").ToList();
        Assert.Equal(14, pfs.Select(b => b.Nid).Distinct().Count());
        var facade = HleBindings.Modules.SelectMany(m => m.Bindings()).Where(b => b.Library == "ScePfsFacadeForKernel").ToList();
        Assert.Equal(6, facade.Select(b => b.Nid).Distinct().Count());
    }

    private uint VfsInfo(string name)
    {
        uint info = _harness.Alloc(0x20);
        GuestMemory.Fill(info, 0, 0x20);
        GuestMemory.W32(info + 4, _harness.Str(name));
        return info;
    }

    [Fact]
    public void DriversRegisterOnceAndLeaveWhenUnreferenced()
    {
        uint first = VfsInfo("exfat");
        Assert.Equal(0, Call(Io, "ksceVfsAddVfs", first));
        Assert.Equal(Exist, Call(Io, "ksceVfsAddVfs", VfsInfo("exfat")));
        uint second = VfsInfo("tty");
        Assert.Equal(0, Call(Io, "ksceVfsAddVfs", second));
        Assert.Equal(first, GuestMemory.R32(second + 0x1C));
        GuestMemory.W32(first + 0xC, 1);
        Assert.Equal(Busy, Call(Io, "ksceVfsDeleteVfs", _harness.Str("exfat"), 0u));
        GuestMemory.W32(first + 0xC, 0);
        uint found = _harness.Alloc(4);
        Assert.Equal(0, Call(Io, "ksceVfsDeleteVfs", _harness.Str("exfat"), found));
        Assert.Equal(first, GuestMemory.R32(found));
        Assert.Equal(NoEnt, Call(Io, "ksceVfsDeleteVfs", _harness.Str("exfat"), 0u));
    }

    [Fact]
    public void VnodesFilesAndLocks()
    {
        uint mount = _harness.Alloc(0x100);
        GuestMemory.Fill(mount, 0, 0x100);
        uint vops = _harness.Alloc(0x80);
        GuestMemory.Fill(vops, 0, 0x80);
        uint vpp = _harness.Alloc(4);
        Assert.Equal(0, Call(Io, "vfsGetNewVnode", mount, vops, 0u, vpp));
        uint vnode = GuestMemory.R32(vpp);
        Assert.Equal(vops, GuestMemory.R32(vnode + 0x40));
        Assert.Equal(mount, GuestMemory.R32(vnode + 0x4C));
        Assert.Equal(0x8000u, GuestMemory.R32(vnode + 0x74));
        Assert.Equal(Perm, Call(Io, "vfsUnlockVnode", vnode));
        Assert.Equal(0, Call(Io, "vfsLockVnode", vnode));
        Assert.Equal(1, Call(Io, "vfsIsOwnerVnode", vnode));
        Assert.Equal(0, Call(Io, "vfsLockVnode", vnode));
        Assert.Equal(0, Call(Io, "vfsUnlockVnode", vnode));
        Assert.Equal(1, Call(Io, "vfsIsOwnerVnode", vnode));
        Assert.Equal(0, Call(Io, "vfsUnlockVnode", vnode));
        Assert.Equal(0, Call(Io, "vfsIsOwnerVnode", vnode));
        Assert.Equal(0, Call(Io, "vfsLockMnt", mount));
        Assert.Equal(1, Call(Io, "vfsIsLockedMnt", mount));
        Assert.Equal(0, Call(Io, "vfsUnlockMnt", mount));
        Assert.Equal(0, Call(Io, "vfsIsLockedMnt", mount));

        uint filep = _harness.Alloc(4);
        int uid = Call(Io, "vfsAllocateFile", vnode, filep, 0u);
        Assert.True(uid > 0); // the file object's UID, the descriptor the module keeps
        uint file = GuestMemory.R32(filep);
        Assert.Equal(vnode, GuestMemory.R32(file + 0x18));
        Assert.Equal(Inval, Call(Io, "vfsFreeVnode", file));
        Assert.Equal(0, Call(Io, "vfsFreeFile", vnode, (uint)uid));

        // an operation the driver does not provide on an active vnode, and no vnode at all
        GuestMemory.W32(vnode + 0x74, 1);
        Assert.Equal(Unsup, Call(Io, "ksceVopOpen", vnode, 0u, 0u, 0u));
        Assert.Equal(Inval, Call(Io, "ksceVopOpen", 0u, 0u, 0u, 0u));
        Assert.Equal(Inval, Call(Io, "ksceVopGetstat", vnode, 0u, 0u));
        Assert.Equal(Unsup, Call(Io, "ksceVfsOpDevctl", mount, 0u, 0u, 0u, 0u, 0u, 0u));
        GuestMemory.W32(mount + 0x48, 0x10);
        Assert.Equal(NotMounted, Call(Io, "ksceVfsOpDevctl", mount, 0u, 0u, 0u, 0u, 0u, 0u));
        Assert.Equal(0, Call(Io, "vfsFreeVnode", vnode));
        Assert.Equal(Inval, Call(Io, "vfsFreeVnode", vnode));

        Assert.Equal(Inval, Call(Io, "vfsMount", 0u));
        Assert.Equal(Inval, Call(Io, "vfsMount", mount)); // no root path
    }

    [Fact]
    public void PfsMountsServeTheSourceAtTheMountPoint()
    {
        string? host = _harness.Emu.Vfs.Resolve("ux0:pfssrc", true);
        Assert.NotNull(host);
        Directory.CreateDirectory(host!);
        File.WriteAllText(Path.Combine(host!, "data.bin"), "x");
        uint source = _harness.Str("ux0:pfssrc");
        uint point = _harness.Str("pfst");
        Assert.Equal(Inval, Call(Io, "kscePfsMount2", source, _harness.Str(""), 0u, 6u));
        Assert.Equal(Inval, Call(Io, "kscePfsMount2", source, _harness.Str(new string('m', 32)), 0u, 6u));
        Assert.Equal(NoEnt, Call(Io, "kscePfsMount2", _harness.Str("ux0:missing"), point, 0u, 6u));
        Assert.Equal(0, Call(Io, "kscePfsMount2", source, point, 0u, 6u));
        Assert.Equal(Exist, Call(Io, "kscePfsMount2", source, point, 0u, 6u));
        Assert.Equal(Path.Combine(host!, "data.bin"), _harness.Emu.Vfs.Resolve("pfst0:data.bin"));
        Assert.Equal(NotMounted, Call(Io, "kscePfsMount", source, point, 0u, 0u, 0u, 0x28u)); // no authority approved
        uint state = _harness.Alloc(4);
        Assert.Equal(0, Call(Io, "kscePfs_4C148288", point, state));
        Assert.Equal(0u, GuestMemory.R32(state));
        Assert.Equal(0, Call(Io, "kscePfsApprove", point, 0u, 0u, 0u));
        Assert.Equal(6, Call(Io, "kscePfsMount", source, point, 0u, 0u, 0u, 0x28u));
        Assert.Equal(0, Call(Io, "kscePfs_4C148288", point, state));
        Assert.Equal(1u, GuestMemory.R32(state));
        Assert.Equal(0, Call(Io, "kscePfsDisapprove", point, 0u, 0u, 0u));
        Assert.Equal(0, Call(Io, "kscePfsUnmount", point));
        Assert.False(_harness.Emu.Vfs.IsMounted("pfst0"));
        Assert.Equal(NotMounted, Call(Io, "kscePfsUnmount", point));
        Assert.Equal(NotMounted, Call(Io, "kscePfsApprove", point, 0u, 0u, 0u));
        Assert.Equal(0, Call(Io, "kscePfsAcidDirSet", source, _harness.Str("PCSA00000")));
        Assert.True(Call(Io, "kscePfsAcidDirSet", _harness.Str("ux0:missing"), _harness.Str("PCSA00000")) < 0);
        Assert.Equal(-1, Call(Io, "kscePfsFacade_04352D52"));
    }

    /// <summary>
    /// kscePfsMount's arguments as pfsmgr 3.74 takes them (ScePfsMgrForKernel 0xA772209C, decompiled, study only):
    /// (source, mount point, program authority ID in the third and fourth words, key, mode). A mode other than 0x28 is
    /// Mount2(source, mount point, key, mode) then Approve(mount point, authority ID); mode 0x28 answers the mode of a
    /// mount that authority ID is approved on. SceAppMgr mounts applications with mode 5 and an authority ID such as
    /// 0x2F00000000000001. Fails when the call reads the third and fourth words as the key and the mode.
    /// </summary>
    [Fact]
    public void PfsMountTakesTheAuthorityIdTheKeyAndTheMode()
    {
        Directory.CreateDirectory(_harness.Emu.Vfs.Resolve("ux0:pfsapp", true)!);
        uint source = _harness.Str("ux0:pfsapp");
        uint point = _harness.Str("tsa1b2c3d4e5f6d");
        uint key = _harness.Alloc(0x10);
        const uint AuthorityLow = 1, AuthorityHigh = 0x2F000000;

        Assert.Equal(0, Call(Io, "kscePfsMount", source, point, AuthorityLow, AuthorityHigh, key, 5u));
        Assert.True(_harness.Emu.Vfs.IsMounted("tsa1b2c3d4e5f6d0"));
        Assert.Equal(5, Call(Io, "kscePfsMount", 0u, point, AuthorityLow, AuthorityHigh, 0u, 0x28u));
        Assert.Equal(NotMounted, Call(Io, "kscePfsMount", 0u, point, 2u, AuthorityHigh, 0u, 0x28u)); // another program
        Assert.Equal(NotMounted, Call(Io, "kscePfsMount", 0u, _harness.Str("tsnone"), AuthorityLow, AuthorityHigh, 0u, 0x28u));
        Assert.Equal(Inval, Call(Io, "kscePfsMount", 0u, 0u, AuthorityLow, AuthorityHigh, 0u, 0x28u));

        // the authority table: one entry per ID, 16 per mount
        Assert.Equal(Exist, Call(Io, "kscePfsApprove", point, 0u, AuthorityLow, AuthorityHigh));
        for (uint id = 2; id <= 16; id++)
        {
            Assert.Equal(0, Call(Io, "kscePfsApprove", point, 0u, id, AuthorityHigh));
        }

        Assert.Equal(unchecked((int)0x8001001C), Call(Io, "kscePfsApprove", point, 0u, 17u, AuthorityHigh));
        Assert.Equal(0, Call(Io, "kscePfsDisapprove", point, 0u, AuthorityLow, AuthorityHigh));
        Assert.Equal(NoEnt, Call(Io, "kscePfsDisapprove", point, 0u, AuthorityLow, AuthorityHigh));
        Assert.Equal(NotMounted, Call(Io, "kscePfsMount", 0u, point, AuthorityLow, AuthorityHigh, 0u, 0x28u));
        Assert.Equal(0, Call(Io, "kscePfsUnmount", point));
        Assert.False(_harness.Emu.Vfs.IsMounted("tsa1b2c3d4e5f6d0"));
    }

    /// <summary>The modes pfsmgr 3.74 mounts (game data/save data 2, 3, 5, 6, 7, 9, 10, 13; AC 4, 8, 12; redirect 0,
    /// 20 to 23; any other 0x80142109) and its key rules (mode 10 takes none; 2, 3, 10 and 13 need one on a retail
    /// unit), checked before the source.</summary>
    [Fact]
    public void PfsMountAcceptsTheFirmwareModes()
    {
        uint source = _harness.Str("ux0:pfsmissing");
        uint point = _harness.Str("pfsm");
        uint key = _harness.Alloc(0x10);
        const int BadMode = unchecked((int)0x80142109);
        uint[] mounted = [0, 2, 3, 4, 5, 6, 7, 8, 9, 12, 13, 20, 21, 22, 23];
        for (uint mode = 0; mode < 0x30; mode++)
        {
            int expected = Array.IndexOf(mounted, mode) >= 0 ? NoEnt : BadMode;
            if (mode == 10)
            {
                expected = Inval; // with a key
            }

            Assert.Equal(expected, Call(Io, "kscePfsMount2", source, point, key, mode));
        }

        Assert.Equal(Inval, Call(Io, "kscePfsMount2", source, point, 0u, 10u)); // no key on a retail unit
        Assert.Equal(Inval, Call(Io, "kscePfsMount2", source, point, 0u, 2u));
        Assert.Equal(NoEnt, Call(Io, "kscePfsMount2", source, point, 0u, 5u));
        Assert.Equal(Inval, Call(Io, "kscePfsMount2", _harness.Str("ux0:" + new string('p', 59)), point, key, 5u));
    }

    [Fact]
    public void KernelServicesSeeTheOpenFiles()
    {
        string? host = _harness.Emu.Vfs.Resolve("ux0:iok.bin", true);
        File.WriteAllBytes(host!, new byte[0x30]);
        int fd = Call(Io, "sceIoOpen", "ux0:iok.bin", 1, 0);
        Assert.True(fd > 0);
        uint list = _harness.Alloc(0x40);
        uint count = _harness.Alloc(4);
        Assert.Equal(0, Call(Io, "ksceIoGetGUIDFdListForDebugger", 0, list, 16u, count));
        Assert.True(GuestMemory.R32(count) >= 1);
        Assert.Contains((uint)fd, Enumerable.Range(0, (int)GuestMemory.R32(count)).Select(i => GuestMemory.R32(list + (uint)i * 4)));
        uint info = _harness.Alloc(8);
        Assert.Equal(0, Call(Io, "ksceIoGetFileInfo", fd, 0, info, 0u));
        Assert.Equal(0x30L, GuestMemory.Read<long>(info));
        Assert.Equal(unchecked((int)0x80010009), Call(Io, "ksceIoGetFileInfo", 0x7FFF, 0, info, 0u));
        Assert.Equal(0, Call(Io, "sceIoClose", fd));

        uint type = _harness.Alloc(4);
        Assert.Equal(0, Call(Io, "ksceIoGetMediaType", 0, _harness.Str("ux0:data"), 0u, type));
        Assert.Equal(4u, GuestMemory.R32(type));
        Assert.Equal(0, Call(Io, "ksceIoGetMediaType", 0, _harness.Str("zz9:data"), 0u, type));
        Assert.Equal(8u, GuestMemory.R32(type));

        int id = Call(Io, "ksceIoCreateMountEvent", 0x81000001u, 1u);
        Assert.True(id > 0);
        Assert.Equal(0, Call(Io, "ksceIoDeleteMountEvent", id));
        Assert.Equal(unchecked((int)0x80028002), Call(Io, "ksceIoDeleteMountEvent", id));
        Assert.Equal(Inval, Call(Io, "ksceIoCreateErrorEvent", 0u, 0u, 0u));
        Assert.Equal(Inval, Call(Io, "ksceIoMount", 0u, 0u, 0u, 0u, 0u, 0u));
        Assert.Equal(0, Call(Io, "ksceIoMount", 0x800u, 0u, 0u, 0u, 0u, 0u));
        Assert.Equal(0, Call(Io, "ksceIoSetProcessDefaultPriorityForSystem", 0x30));
        Assert.Equal(0x30, Call(Io, "ksceIoGetProcessDefaultPriorityForSystem"));
        Assert.Equal(0x30, Call(Io, "sceIoGetProcessDefaultPriority"));
    }
}
