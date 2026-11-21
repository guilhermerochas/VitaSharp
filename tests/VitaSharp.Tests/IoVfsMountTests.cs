using System.Text;
using VitaSharp.Core.Hle;
using VitaSharp.Core.Hle.Modules;
using VitaSharp.Core.Loader;
using VitaSharp.Core.Memory;
using Xunit;
using static VitaSharp.Tests.HleHarness;

namespace VitaSharp.Tests;

/// <summary>
/// A guest file system driver (a synthetic kernel plugin's, its ops guest-callable stubs) registered with
/// ksceVfsAddVfs and mounted with vfsMount, as SceIofilemgr 3.74 does it (0xB62DE9A6, 0x81004718 for the assign name,
/// the vnode op wrappers, read in the decompiled module): the mount calls the driver's mount, set_root and devctl
/// 0x3802 ops; sceIoOpen / Read / Lseek / Close / Dopen / Dread / Dclose / Getstat / Mkdir / Remove on the mount's assign
/// name or root path walk the driver's lookup op from the root vnode and end in its vnode ops; the driver's own I/O on
/// its block device goes to the emulator's host folders; vfsUmount gives the path back.
/// </summary>
[Collection("guest")]
public sealed class IoVfsMountTests(GuestMemoryFixture fixture) : IDisposable
{
    private const int NoEnt = unchecked((int)0x80010002), Busy = unchecked((int)0x80010010);
    private const int Inval = unchecked((int)0x80010016), Exist = unchecked((int)0x80010011);
    private const uint ORdOnly = 1, OWrOnly = 2, OCreat = 0x200;

    private readonly HleHarness _h = new(fixture, "vfs-mount");
    private readonly List<string> _calls = [];
    private readonly Dictionary<string, uint> _vnodes = [];
    private readonly Dictionary<uint, string> _names = [];
    private readonly Dictionary<string, byte[]> _contents = new() { ["hello.txt"] = Encoding.ASCII.GetBytes("hi from vfs") };
    private readonly Dictionary<uint, int> _positions = [];
    private uint _stubs;
    private uint _next;
    private uint _vops;
    private uint _root;
    private string _deviceSeen = "";
    private string _deviceRead = "";

    public void Dispose() => _h.Dispose();

    private IoFileMgr Io => _h.Module<IoFileMgr>();

    private uint Stub(string name, Func<uint, int> body)
    {
        if (_stubs == 0)
        {
            _stubs = _h.Emu.Memory.Alloc(0x1000, "vfs test ops");
        }

        uint at = _stubs + _next;
        _next += 16;
        Linker.WriteHleStub(at, _h.Emu.Hle.RegisterHostFunction("vfstest:" + name, context =>
        {
            _calls.Add(name);
            context.Return(body(context.Gpr(0)));
        }), 0);
        _h.Emu.Code.Invalidate(at, 16);
        return at;
    }

    private static string Element(uint vfsPath) => GuestMemory.ReadCString(GuestMemory.R32(vfsPath), (int)GuestMemory.R32(vfsPath + 4));

    private uint NewVnode(uint mount, string name)
    {
        if (_vnodes.TryGetValue(name, out uint known))
        {
            return known;
        }

        uint vpp = _h.Alloc(4);
        Assert.Equal(0, Call(Io, "vfsGetNewVnode", mount, _vops, 0u, vpp));
        uint vnode = GuestMemory.R32(vpp);
        _vnodes[name] = vnode;
        _names[vnode] = name;
        return vnode;
    }

    /// <summary>Registers the driver "tvfs" (its VFS op table and default vnode op table) and returns its SceVfsInfo.</summary>
    private uint RegisterDriver()
    {
        uint vfsOps = _h.Alloc(0x40);
        GuestMemory.Fill(vfsOps, 0, 0x40);
        GuestMemory.W32(vfsOps + 0x00, Stub("mount", args =>
        {
            uint device = GuestMemory.R32(args + 4);
            _deviceSeen = device == 0 ? "" : Element(device);
            // the driver reads its block device through the emulator's own files
            string devicePath = device == 0 ? "" : GuestMemory.ReadCString(GuestMemory.R32(device + 8));
            int fd = Io.sceIoOpen(devicePath, (int)ORdOnly, 0);
            if (fd > 0)
            {
                uint buffer = _h.Alloc(16);
                int read = Io.sceIoRead(fd, buffer, 16);
                _deviceRead = GuestMemory.ReadCString(buffer, read);
                Io.sceIoClose(fd);
            }

            return 0;
        }));
        GuestMemory.W32(vfsOps + 0x04, Stub("umount", _ => 0));
        GuestMemory.W32(vfsOps + 0x08, Stub("set_root", args =>
        {
            _root = GuestMemory.R32(args + 8);
            _names[_root] = "/";
            return 0;
        }));
        GuestMemory.W32(vfsOps + 0x2C, Stub("devctl", args => GuestMemory.R32(args + 8) == 0x3802 ? 0 : -1));

        _vops = _h.Alloc(0x80);
        GuestMemory.Fill(_vops, 0, 0x80);
        GuestMemory.W32(_vops + 0x00, Stub("open", _ => 0));
        GuestMemory.W32(_vops + 0x04, Stub("create", args =>
        {
            string name = Element(GuestMemory.R32(args + 8));
            _contents[name] = [];
            GuestMemory.W32(GuestMemory.R32(args + 4), NewVnode(GuestMemory.R32(GuestMemory.R32(args) + 0x4C), name));
            return 0;
        }));
        GuestMemory.W32(_vops + 0x08, Stub("close", _ => 0));
        GuestMemory.W32(_vops + 0x0C, Stub("lookup", args =>
        {
            string name = Element(GuestMemory.R32(args + 8));
            if (!_contents.ContainsKey(name) && name != "sub")
            {
                return NoEnt;
            }

            GuestMemory.W32(GuestMemory.R32(args + 4), NewVnode(GuestMemory.R32(GuestMemory.R32(args) + 0x4C), name));
            return 0;
        }));
        GuestMemory.W32(_vops + 0x10, Stub("read", args =>
        {
            byte[] data = _contents[_names[GuestMemory.R32(args)]];
            uint file = GuestMemory.R32(args + 4);
            int position = _positions.GetValueOrDefault(file);
            int count = Math.Min((int)GuestMemory.R32(args + 12), data.Length - position);
            data.AsSpan(position, count).CopyTo(GuestMemory.Span(GuestMemory.R32(args + 8), count));
            _positions[file] = position + count;
            return count;
        }));
        GuestMemory.W32(_vops + 0x14, Stub("write", args =>
        {
            string name = _names[GuestMemory.R32(args)];
            byte[] added = GuestMemory.ReadSpan(GuestMemory.R32(args + 8), (int)GuestMemory.R32(args + 12)).ToArray();
            _contents[name] = [.. _contents[name], .. added];
            return added.Length;
        }));
        GuestMemory.W32(_vops + 0x18, Stub("lseek", args =>
        {
            int offset = (int)GuestMemory.R32(args + 8);
            _positions[GuestMemory.R32(args + 4)] = offset;
            return offset;
        }));
        GuestMemory.W32(_vops + 0x20, Stub("remove", args => _contents.Remove(Element(GuestMemory.R32(args + 8))) ? 0 : NoEnt));
        GuestMemory.W32(_vops + 0x24, Stub("mkdir", _ => 0));
        GuestMemory.W32(_vops + 0x2C, Stub("dopen", _ => 0));
        GuestMemory.W32(_vops + 0x30, Stub("dclose", _ => 0));
        GuestMemory.W32(_vops + 0x34, Stub("dread", args =>
        {
            uint file = GuestMemory.R32(args + 4);
            int index = _positions.GetValueOrDefault(file);
            string[] names = [.. _contents.Keys.Order(StringComparer.Ordinal)];
            if (index >= names.Length)
            {
                return 0;
            }

            _positions[file] = index + 1;
            GuestMemory.WriteCString(GuestMemory.R32(args + 8) + 88, names[index], 256);
            return 1;
        }));
        GuestMemory.W32(_vops + 0x38, Stub("getstat", args =>
        {
            uint stat = GuestMemory.R32(args + 8);
            string name = _names[GuestMemory.R32(args)];
            GuestMemory.W32(stat, name == "/" ? 0x11FFu : 0x21FFu);
            GuestMemory.Write(stat + 8, (long)(_contents.TryGetValue(name, out byte[]? data) ? data.Length : 0));
            return 0;
        }));

        uint info = _h.Alloc(0x20);
        GuestMemory.Fill(info, 0, 0x20);
        GuestMemory.W32(info, vfsOps);
        GuestMemory.W32(info + 4, _h.Str("tvfs"));
        GuestMemory.W32(info + 0x14, _vops);
        Assert.Equal(0, Call(Io, "ksceVfsAddVfs", info));
        return info;
    }

    /// <summary>A SceVfsMountParam: root path, block device, fs type, flags, driver name, misc { assign name, fs name,
    /// block device, block device without partition }.</summary>
    private uint MountParam(string root, string? device, uint fsType, uint flags, string driver = "tvfs", string assign = "tvfs0:")
    {
        uint misc = _h.Alloc(0x14);
        GuestMemory.Fill(misc, 0, 0x14);
        GuestMemory.W32(misc, _h.Str(assign));
        GuestMemory.W32(misc + 4, _h.Str(driver));
        GuestMemory.W32(misc + 8, _h.Str("ux0:tvfs.img"));
        uint param = _h.Alloc(0x20);
        GuestMemory.Fill(param, 0, 0x20);
        GuestMemory.W32(param, _h.Str(root));
        GuestMemory.W32(param + 4, device == null ? 0 : _h.Str(device));
        GuestMemory.W32(param + 8, fsType);
        GuestMemory.W32(param + 0xC, flags);
        GuestMemory.W32(param + 0x10, _h.Str(driver));
        GuestMemory.W32(param + 0x18, misc);
        return param;
    }

    private void MountTvfs()
    {
        string? image = _h.Emu.Vfs.Resolve("ux0:tvfs.img", true);
        Assert.NotNull(image);
        File.WriteAllText(image!, "IMG1");
        RegisterDriver();
        Assert.Equal(0, Call(Io, "vfsMount", MountParam("/tvfs", null, 1, 2)));
    }

    [Fact]
    public void TheMountCallsTheDriversMountSetRootAndDevctlAndItsDeviceIsTheHostFile()
    {
        _h.OnGuest(_ =>
        {
            MountTvfs();
            Assert.Equal(["mount", "set_root", "devctl"], _calls);
            Assert.Equal("tvfs.img", _deviceSeen);
            Assert.Equal("IMG1", _deviceRead);
            Assert.NotEqual(0u, _root);
            Assert.Equal(_vops, GuestMemory.R32(_root + 0x40));
            uint mount = GuestMemory.R32(_root + 0x4C);
            Assert.Equal("/tvfs", GuestMemory.ReadCString(mount + 0x80));
            Assert.Equal(0u, GuestMemory.R32(mount + 0x48) & 0x100); // no longer being mounted
        });
    }

    [Fact]
    public void OpenReadSeekAndCloseReachTheDriversOpsByAssignNameAndRootPath()
    {
        _h.OnGuest(_ =>
        {
            MountTvfs();
            _calls.Clear();
            int fd = Io.sceIoOpen("tvfs0:hello.txt", (int)ORdOnly, 0);
            Assert.True(fd > 0, $"open 0x{fd:X8}");
            Assert.Equal(["lookup", "open"], _calls);
            uint buffer = _h.Alloc(32);
            Assert.Equal(11, Io.sceIoRead(fd, buffer, 32));
            Assert.Equal("hi from vfs", GuestMemory.ReadCString(buffer, 11));
            Assert.Equal(3L, Io.sceIoLseek(fd, 3, 0));
            Assert.Equal(4, Io.sceIoRead(fd, buffer, 4));
            Assert.Equal("from", GuestMemory.ReadCString(buffer, 4));
            Assert.Equal(0, Io.sceIoClose(fd));
            Assert.Equal("close", _calls[^1]);

            int byRoot = Io.sceIoOpen("/tvfs/hello.txt", (int)ORdOnly, 0);
            Assert.True(byRoot > 0);
            Assert.Equal(0, Io.sceIoClose(byRoot));
            Assert.Equal(NoEnt, Io.sceIoOpen("tvfs0:missing.txt", (int)ORdOnly, 0));
        });
    }

    [Fact]
    public void CreateWriteStatDirectoryAndRemoveReachTheDriver()
    {
        _h.OnGuest(_ =>
        {
            MountTvfs();
            _calls.Clear();
            int fd = Io.sceIoOpen("tvfs0:new.txt", (int)(OWrOnly | OCreat), 0x1B6);
            Assert.True(fd > 0);
            Assert.Equal(["lookup", "create", "open"], _calls);
            Assert.Equal(5, Io.sceIoWrite(fd, _h.Str("abcde"), 5));
            Assert.Equal(0, Io.sceIoClose(fd));
            Assert.Equal("abcde", Encoding.ASCII.GetString(_contents["new.txt"]));
            Assert.Equal(Exist, Io.sceIoOpen("tvfs0:new.txt", (int)(OWrOnly | OCreat | 0x800), 0));

            uint stat = _h.Alloc(0x58);
            Assert.Equal(0, Io.sceIoGetstat("tvfs0:new.txt", stat));
            Assert.Equal(5L, GuestMemory.Read<long>(stat + 8));
            Assert.Equal(0, Io.sceIoGetstat("tvfs0:", stat));
            Assert.Equal(0x11FFu, GuestMemory.R32(stat));

            int dir = Io.sceIoDopen("tvfs0:");
            Assert.True(dir > 0);
            uint dirent = _h.Alloc(352);
            var listed = new List<string>();
            while (Io.sceIoDread(dir, dirent) > 0)
            {
                listed.Add(GuestMemory.ReadCString(dirent + 88));
            }

            Assert.Equal(["hello.txt", "new.txt"], listed);
            Assert.Equal(0, Io.sceIoDclose(dir));
            Assert.Contains("dclose", _calls);

            _calls.Clear();
            Assert.Equal(0, Io.sceIoMkdir("tvfs0:sub2", 0x1FF));
            Assert.Equal(["mkdir"], _calls);
            Assert.Equal(0, Io.sceIoRemove("tvfs0:new.txt"));
            Assert.False(_contents.ContainsKey("new.txt"));
        });
    }

    [Fact]
    public void BadRequestsAndTheUnmount()
    {
        _h.OnGuest(_ =>
        {
            Assert.Equal(Inval, Call(Io, "vfsMount", 0u));
            Assert.Equal(Inval, Call(Io, "vfsMount", MountParam("/tvfs", null, 1, 2))); // no driver registered yet
            MountTvfs();
            Assert.Equal(Busy, Call(Io, "vfsMount", MountParam("/tvfs", null, 1, 2)));
            Assert.Equal(Inval, Call(Io, "vfsMount", MountParam("/other", null, 3, 2))); // fs type 3 is not a root's
            Assert.Equal(Inval, Call(Io, "vfsMount", MountParam("/other", null, 1, 5))); // a stacked mount needs a device
            Assert.Equal(Exist, Call(Io, "vfsMount", MountParam("/", null, 4, 6))); // the root is the emulator's
            uint info = GuestMemory.R32(GuestMemory.R32(_root + 0x4C) + 0x5C);
            Assert.Equal(1u, GuestMemory.R32(info + 0xC)); // the mount's reference to the driver
            Assert.Equal(Busy, Call(Io, "ksceVfsDeleteVfs", _h.Str("tvfs"), 0u));

            uint unmount = _h.Alloc(8);
            GuestMemory.W32(unmount, _h.Str("/tvfs"));
            GuestMemory.W32(unmount + 4, 0);
            _calls.Clear();
            Assert.Equal(0, Call(Io, "vfsUmount", unmount));
            Assert.Equal(["umount"], _calls);
            Assert.Equal(0u, GuestMemory.R32(info + 0xC));
            Assert.Equal(NoEnt, Call(Io, "vfsUmount", unmount));
            Assert.True(Io.sceIoOpen("tvfs0:hello.txt", (int)ORdOnly, 0) < 0); // the device is no longer served
            Assert.Equal(0, Call(Io, "ksceVfsDeleteVfs", _h.Str("tvfs"), 0u));
        });
    }

    /// <summary>The vnode read wrapper as the module has it (0x570388A5): the driver's op gets { vnode, file, buffer,
    /// size } and returns the count, which goes to *result while the call returns 0 (an error: both the error).</summary>
    [Fact]
    public void TheReadWrapperPutsTheCountInTheResultAndReturnsZero()
    {
        _h.OnGuest(_ =>
        {
            MountTvfs();
            uint vnode = NewVnode(GuestMemory.R32(_root + 0x4C), "hello.txt");
            uint filep = _h.Alloc(4);
            Assert.Equal(0, Call(Io, "vfsAllocateFile", vnode, filep));
            uint file = GuestMemory.R32(filep);
            uint result = _h.Word(0xFFFFFFFF);
            uint buffer = _h.Alloc(16);
            Assert.Equal(0, Call(Io, "ksceVopRead", vnode, file, buffer, 5u, result));
            Assert.Equal(5u, GuestMemory.R32(result));
            Assert.Equal("hi fr", GuestMemory.ReadCString(buffer, 5));
            _calls.Clear();
            Assert.Equal(0, Call(Io, "ksceVopRead", vnode, file, buffer, 0u, result));
            Assert.Equal(0u, GuestMemory.R32(result));
            Assert.Empty(_calls); // a size of 0 does not reach the driver
        });
    }
}
