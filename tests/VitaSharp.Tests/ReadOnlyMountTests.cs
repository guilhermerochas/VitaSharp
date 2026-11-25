using VitaSharp.Core.Hle.Modules;
using static VitaSharp.Tests.HleHarness;

namespace VitaSharp.Tests;

/// <summary>
/// FW 3.74 iofilemgr keeps a read-only flag in each mount point and answers EROFS (0x8001001E) to anything that would
/// change a read-only mount: an open for writing, a create, remove, mkdir, rmdir and rename on app0 (the application's
/// image) and on the system partitions os0 and vs0. A partition mounted again with read-write permission (2, as VitaRW
/// and VitaShell's tools do through _vshIoMount) takes writes until it is mounted again with another permission.
/// Before, a guest could change the installed game or the firmware's files on the host.
/// </summary>
[Collection("guest")]
public sealed class ReadOnlyMountTests(GuestMemoryFixture fixture) : IDisposable
{
    private const int ReadOnlyFileSystem = unchecked((int)0x8001001E);
    private const int RdOnly = 1, WrOnly = 2, Create = 0x200, Truncate = 0x400;

    private readonly HleHarness _harness = new(fixture, "rofs");

    private IoFileMgr Io => _harness.Module<IoFileMgr>();

    public void Dispose() => _harness.Dispose();

    private string Host(string guest) => _harness.Emu.Vfs.Resolve(guest)!;

    private void HostFile(string guest, string text)
    {
        string host = Host(guest);
        Directory.CreateDirectory(Path.GetDirectoryName(host)!);
        File.WriteAllText(host, text);
    }

    [Fact]
    public void TheSystemPartitionsAndApp0RefuseWrites()
    {
        HostFile("vs0:data/kept.txt", "firmware");
        int fd = Call(Io, "sceIoOpen", "vs0:data/kept.txt", RdOnly, 0);
        Assert.True(fd > 0); // reading is fine
        Assert.Equal(0, Call(Io, "sceIoClose", fd));

        Assert.Equal(ReadOnlyFileSystem, Call(Io, "sceIoOpen", "vs0:data/kept.txt", WrOnly, 0));
        Assert.Equal(ReadOnlyFileSystem, Call(Io, "sceIoOpen", "vs0:data/kept.txt", RdOnly | Truncate, 0));
        Assert.Equal(ReadOnlyFileSystem, Call(Io, "sceIoOpen", "vs0:data/new.txt", WrOnly | Create, 0x1B6));
        Assert.Equal(ReadOnlyFileSystem, Call(Io, "sceIoRemove", "vs0:data/kept.txt"));
        Assert.Equal(ReadOnlyFileSystem, Call(Io, "sceIoMkdir", "vs0:newdir", 0x1FF));
        Assert.Equal(ReadOnlyFileSystem, Call(Io, "sceIoRmdir", "vs0:data"));
        Assert.Equal(ReadOnlyFileSystem, Call(Io, "sceIoRename", "vs0:data/kept.txt", "vs0:data/moved.txt"));
        Assert.Equal(ReadOnlyFileSystem, Call(Io, "sceIoOpen", "os0:kd/new.skprx", WrOnly | Create, 0x1B6));
        Assert.Equal(ReadOnlyFileSystem, Call(Io, "sceIoOpen", "app0:eboot.bin", WrOnly | Create, 0x1B6));
        Assert.Equal(ReadOnlyFileSystem, Call(Io, "sceIoRename", "ux0:data/a.txt", "app0:a.txt"));

        Assert.Equal("firmware", File.ReadAllText(Host("vs0:data/kept.txt")));
        Assert.False(File.Exists(Host("vs0:data/new.txt")));
        Assert.False(Directory.Exists(Host("vs0:newdir")));

        // a writable device is not affected
        int ux0 = Call(Io, "sceIoOpen", "ux0:data/rofs.txt", WrOnly | Create, 0x1B6);
        Assert.True(ux0 > 0);
        Assert.Equal(0, Call(Io, "sceIoClose", ux0));
    }

    [Fact]
    public void VsZeroMountedAgainReadWriteTakesWritesUntilTheDefaultMount()
    {
        VshBridge bridge = _harness.Module<VshBridge>();
        uint buffer = _harness.Alloc(0x18);
        Assert.Equal(0, Call(bridge, "vshIoUmount", 0x300, 0, 0, 0));
        Assert.Equal(0, Call(bridge, "_vshIoMount", 0x300, 0u, 2, buffer)); // read-write

        int fd = Call(Io, "sceIoOpen", "vs0:data/patched.txt", WrOnly | Create, 0x1B6);
        Assert.True(fd > 0);
        Assert.Equal(0, Call(Io, "sceIoClose", fd));
        Assert.True(File.Exists(Host("vs0:data/patched.txt")));
        Assert.Equal(ReadOnlyFileSystem, Call(Io, "sceIoOpen", "os0:kd/new.skprx", WrOnly | Create, 0x1B6)); // os0 stays

        Assert.Equal(0, Call(bridge, "_vshIoMount", 0x300, 0u, 0, buffer)); // the default again
        Assert.Equal(ReadOnlyFileSystem, Call(Io, "sceIoRemove", "vs0:data/patched.txt"));
    }
}
