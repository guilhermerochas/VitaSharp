using System.Reflection;
using VitaSharp.Core.Hle.Modules;
using VitaSharp.Core.Kernel;
using VitaSharp.Core.Memory;

namespace VitaSharp.Tests;

/// <summary>Package B (I/O): FIOS kernel overlays, chstat, async I/O and I/O priorities.</summary>
[Collection("guest")]
public sealed class IoHleTests(GuestMemoryFixture fx)
{
    static object? Invoke<T>(T module, string name, params object?[] args) =>
        typeof(T).GetMethod(name, BindingFlags.Instance | BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public)!.Invoke(module, args);

    static int Call<T>(T module, string name, params object?[] args) => (int)Invoke(module, name, args)!;

    uint WriteOverlay(byte type, byte order, string dst, string src)
    {
        uint p = fx.Memory.Alloc(0x258, "overlay");
        GuestMemory.Fill(p, 0, 0x258);
        GuestMemory.Write(p, type);
        GuestMemory.Write(p + 1, order);
        GuestMemory.WriteCString(p + 0x10, dst, 292);
        GuestMemory.WriteCString(p + 0x134, src, 292);
        return p;
    }

    int Add(Fios2User f, byte order, string dst, string src)
    {
        uint id = fx.Memory.Alloc(4, "id");
        Assert.Equal(0, Call(f, "_sceFiosKernelOverlayAdd", WriteOverlay(0, order, dst, src), new Ptr<int>(id)));
        return (int)GuestMemory.R32(id);
    }

    [Fact]
    public void OverlayGetInfoFillsAllFields()
    {
        var f = new Fios2User();
        int id = Add(f, 5, "app0:/data", "ux0:patch/data");
        uint o = fx.Memory.Alloc(0x258, "out");
        Assert.Equal(0, Call(f, "_sceFiosKernelOverlayGetInfo", id, o));
        Assert.Equal(5, GuestMemory.Read<byte>(o + 1));
        Assert.Equal("app0:data".Length, GuestMemory.Read<ushort>(o + 2));
        Assert.Equal("ux0:patch/data".Length, GuestMemory.Read<ushort>(o + 4));
        Assert.Equal((uint)id, GuestMemory.R32(o + 0xC));
        Assert.Equal("app0:data", GuestMemory.ReadCString(o + 0x10));
        Assert.Equal("ux0:patch/data", GuestMemory.ReadCString(o + 0x134));
        Assert.True(Call(f, "_sceFiosKernelOverlayGetInfo", id + 100, o) < 0);
        Assert.Equal(SceError.ILLEGAL_ADDR, Call(f, "_sceFiosKernelOverlayGetInfo", id, 0u));
    }

    [Fact]
    public void OverlayGetListFiltersByOrder()
    {
        var f = new Fios2User();
        int a = Add(f, 10, "app0:a", "ux0:a"), b = Add(f, 2, "app0:b", "ux0:b"), c = Add(f, 0x90, "app0:c", "ux0:c");
        uint args = fx.Memory.Alloc(0x18, "args"), ids = fx.Memory.Alloc(16, "ids"), actual = fx.Memory.Alloc(4, "actual");
        GuestMemory.Fill(args, 0, 0x18);
        GuestMemory.W32(args, ids);
        GuestMemory.W32(args + 4, 4);
        GuestMemory.W32(args + 8, actual);
        GuestMemory.W32(args + 0xC, 16);
        Assert.Equal(0, Call(f, "_sceFiosKernelOverlayGetList", 0, 0u, 0x7Fu, args));
        Assert.Equal(2u, GuestMemory.R32(actual));
        Assert.Equal((uint)b, GuestMemory.R32(ids));     // lowest order first
        Assert.Equal((uint)a, GuestMemory.R32(ids + 4));
        Assert.Equal(0, Call(f, "_sceFiosKernelOverlayGetList", 0, 0x80u, 0xFFu, args));
        Assert.Equal(1u, GuestMemory.R32(actual));
        Assert.Equal((uint)c, GuestMemory.R32(ids));
    }

    [Fact]
    public void OverlayModifyAndThreadDisable()
    {
        var f = new Fios2User();
        int id = Add(f, 1, "app0:", "ux0:old");
        Assert.Equal("ux0:old/x.bin", f.Resolve("app0:x.bin"));
        Assert.Equal(0, Call(f, "_sceFiosKernelOverlayModify", id, WriteOverlay(0, 1, "app0:", "ux0:new")));
        Assert.Equal("ux0:new/x.bin", f.Resolve("app0:x.bin"));
        Assert.True(Call(f, "_sceFiosKernelOverlayModifyForProcess", 0, id + 50, WriteOverlay(0, 1, "a:", "b:")) < 0);

        Assert.Equal(0, Call(f, "_sceFiosKernelOverlayThreadIsDisabled"));
        Assert.Equal(0, Call(f, "_sceFiosKernelOverlayThreadSetDisabled", 1));
        Assert.Equal(1, Call(f, "_sceFiosKernelOverlayThreadIsDisabled"));
        Assert.Equal("app0:x.bin", f.Resolve("app0:x.bin")); // only privileged overlays apply
        Assert.Equal(0, Call(f, "_sceFiosKernelOverlayThreadSetDisabled", 0));
        Assert.Equal(0, Call(f, "_sceFiosKernelOverlayThreadIsDisabled"));
        Assert.Equal("ux0:new/x.bin", f.Resolve("app0:x.bin"));
    }

    [Fact]
    public void OverlayDirectoryHandlesAreUnsupported()
    {
        var f = new Fios2User();
        const int unsup = unchecked((int)0x80020004);
        Assert.Equal(unsup, Call(f, "_sceFiosKernelOverlayDHOpenSync", 0u, "app0:", 0, 0u));
        Assert.Equal(unsup, Call(f, "_sceFiosKernelOverlayDHCloseSync", 1));
    }

    [Fact]
    public void ChstatSetsTimesSizeAndMode()
    {
        string host = Path.Combine(Path.GetTempPath(), $"vs_chstat_{Guid.NewGuid():N}.bin");
        File.WriteAllBytes(host, new byte[100]);
        try
        {
            uint st = fx.Memory.Alloc(0x58, "stat");
            GuestMemory.Fill(st, 0, 0x58);
            GuestMemory.W32(st, 0x2000 | 0x1B6);       // regular, rw
            GuestMemory.Write(st + 8, 10L);            // size
            // mtime 2010-01-02 03:04:05
            GuestMemory.Write(st + 48, (ushort)2010); GuestMemory.Write(st + 50, (ushort)1); GuestMemory.Write(st + 52, (ushort)2);
            GuestMemory.Write(st + 54, (ushort)3); GuestMemory.Write(st + 56, (ushort)4); GuestMemory.Write(st + 58, (ushort)5);
            Assert.Equal(0, Call<IoFileMgr>(null!, "ApplyChstat", host, st, 0x20 | 0x04 | 0x01, null));
            Assert.Equal(new DateTime(2010, 1, 2, 3, 4, 5), File.GetLastWriteTime(host));
            Assert.Equal(10, new FileInfo(host).Length);
            Assert.False(new FileInfo(host).IsReadOnly);
            GuestMemory.W32(st, 0x2000 | 0x124);       // r--r--r--
            Assert.Equal(0, Call<IoFileMgr>(null!, "ApplyChstat", host, st, 0x01, null));
            Assert.True(new FileInfo(host).IsReadOnly);
            Assert.Equal(SceError.IO_ENOENT, Call<IoFileMgr>(null!, "ApplyChstat", host + ".missing", st, 0x20, null));
        }
        finally
        {
            if (File.Exists(host)) { new FileInfo(host).IsReadOnly = false; File.Delete(host); }
        }
    }

    [Fact]
    public void AsyncOpenWriteCompleteOnTty()
    {
        var io = new IoFileMgr();
        int op = Call(io, "sceIoOpenAsync", "tty0:", 3, 0);
        Assert.True(op > 0);
        int fd = Call(io, "sceIoComplete", op);
        Assert.True(fd > 0);
        Assert.Equal(SceError.UNKNOWN_EVENT_ID, Call(io, "sceIoComplete", op)); // opid freed

        uint buf = fx.Memory.Alloc(16, "buf");
        GuestMemory.WriteCString(buf, "hello");
        int wop = Call(io, "sceIoWriteAsync", fd, buf, 5u);
        Assert.Equal(0, Call(io, "sceIoCancel", wop)); // already complete: nothing to cancel
        Assert.Equal(5, Call(io, "sceIoComplete", wop));
        Assert.Equal(0, Call(io, "sceIoComplete", Call(io, "sceIoCloseAsync", fd)));
        Assert.Equal(SceError.IO_EBADF, Call(io, "sceIoReadAsync", fd, buf, 4u)); // closed fd fails immediately
        Assert.Equal(SceError.IO_EBADF, Call(io, "sceIoCancel", 0x12345));
    }

    [Fact]
    public void IoPriorities()
    {
        var io = new IoFileMgr();
        int fd = Call(io, "sceIoOpen", "tty0:", 3, 0);
        Assert.Equal(0, Call(io, "sceIoSetProcessDefaultPriority", 12));
        Assert.Equal(12, Call(io, "sceIoGetProcessDefaultPriority"));
        Assert.Equal(12, Call(io, "sceIoGetThreadDefaultPriority"));
        Assert.Equal(12, Call(io, "sceIoGetPriority", fd));
        Assert.Equal(0, Call(io, "sceIoSetThreadDefaultPriority", 7));
        Assert.Equal(7, Call(io, "sceIoGetPriority", fd));
        Assert.Equal(0, Call(io, "sceIoSetPriority", fd, 3));
        Assert.Equal(3, Call(io, "sceIoGetPriority", fd));
        Assert.Equal(SceError.IO_EBADF, Call(io, "sceIoSetPriority", 0x7FFF, 3));
        Assert.Equal(SceError.ILLEGAL_PRIORITY, Call(io, "sceIoSetPriority", fd, -1));
    }

    [Fact]
    public void IoctlValidatesFd()
    {
        var io = new IoFileMgr();
        int fd = Call(io, "sceIoOpen", "tty1:", 3, 0);
        uint outBuf = fx.Memory.Alloc(8, "out");
        GuestMemory.Fill(outBuf, 0xAA, 8);
        Assert.Equal(0, Call(io, "sceIoIoctl", fd, 0x1234, 0u, 0u, outBuf, 8u));
        Assert.Equal(0u, GuestMemory.R32(outBuf + 4));
        Assert.Equal(SceError.IO_EBADF, Call(io, "sceIoIoctl", 0x7FFF, 1, 0u, 0u, 0u, 0u));
    }
}
