using System.Reflection;
using System.Runtime.CompilerServices;
using VitaSharp.Core;
using VitaSharp.Core.Hle;
using VitaSharp.Core.Io;
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
        // every request also writes its result (64 bits) to its SceIoAsyncParam, where libpaf reads the fd of an open
        uint param = fx.Memory.Alloc(0x18, "asyncParam");
        GuestMemory.Write(param, -1L);
        int op = Call(io, "sceIoOpenAsync", "tty0:", 3, 0, param);
        Assert.True(op > 0);
        long paramFd = GuestMemory.Read<long>(param);
        int fd = Call(io, "sceIoComplete", op);
        Assert.True(fd > 0);
        Assert.Equal(fd, paramFd);
        Assert.Equal(SceError.UNKNOWN_EVENT_ID, Call(io, "sceIoComplete", op)); // opid freed

        uint buf = fx.Memory.Alloc(16, "buf");
        GuestMemory.WriteCString(buf, "hello");
        int wop = Call(io, "sceIoWriteAsync", fd, buf, 5u, param);
        Assert.Equal(5L, GuestMemory.Read<long>(param));
        Assert.Equal(0, Call(io, "sceIoCancel", wop)); // already complete: nothing to cancel
        Assert.Equal(5, Call(io, "sceIoComplete", wop));
        Assert.Equal(0, Call(io, "sceIoComplete", Call(io, "sceIoCloseAsync", fd, 0u)));
        Assert.Equal(SceError.IO_EBADF, Call(io, "sceIoReadAsync", fd, buf, 4u, 0u)); // closed fd fails immediately
        Assert.Equal(SceError.IO_EBADF, Call(io, "sceIoCancel", 0x12345));
    }

    [Fact]
    public void AsyncSyscallFormsTakeTheParamFromTheWrapperStruct()
    {
        // 3.74 libkernel: sceIoOpenAsync puts its SceIoAsyncParam* at opt+0, sceIoPwriteAsync after the 64-bit offset
        var io = new IoFileMgr();
        uint opt = fx.Memory.Alloc(0x10, "opt"), param = fx.Memory.Alloc(0x18, "asyncParam");
        GuestMemory.W32(opt, param);
        GuestMemory.Write(param, -1L);
        int op = Call(io, "_sceIoOpenAsync", "tty0:", 3, 0, opt);
        int fd = Call(io, "sceIoComplete", op);
        Assert.Equal((long)fd, GuestMemory.Read<long>(param));
        uint buf = fx.Memory.Alloc(16, "buf");
        GuestMemory.Write(opt, 0L);       // offset
        GuestMemory.W32(opt + 8, param);  // then the param
        int wop = Call(io, "_sceIoPwriteAsync", fd, buf, 4u, opt);
        Assert.Equal((long)SceError.IO_EBADF, GuestMemory.Read<long>(param)); // a tty has no file to write at an offset
        Call(io, "sceIoComplete", wop);
        Assert.Equal(0, Call(io, "sceIoComplete", Call(io, "sceIoCloseAsync", fd, 0u)));
    }

    [Fact]
    public void AsyncRegisterFormsTakeTheParamInTheRegisterBeforeTheScratch()
    {
        // 3.74 libkernel: sceIoRemoveAsync/RmdirAsync pass the SceIoAsyncParam* through in r1, Mkdir/Rename/Getstat/Sync
        // in r2, and only then the unwritten scratch struct; iofilemgr queues that register with the request
        string root = Path.Combine(Path.GetTempPath(), "vs_ioasync_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "d"));
        try
        {
            var emu = (Emulator)RuntimeHelpers.GetUninitializedObject(typeof(Emulator));
            void Set(string prop, object v) => typeof(Emulator).GetField($"<{prop}>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(emu, v);
            Set("Memory", fx.Memory);
            Set("Vfs", new Vfs(root));
            Set("Hle", new HleRegistry(emu));
            emu.Vfs.Mount("ux0", Path.Combine(root, "d"));
            var io = new IoFileMgr { Emu = emu };
            uint opt = fx.Memory.Alloc(0x14, "opt"), param = fx.Memory.Alloc(0x18, "asyncParam"), stat = fx.Memory.Alloc(0x60, "stat");
            GuestMemory.W32(opt, 0x5A5A5A5A);
            GuestMemory.Write(param, -1L);
            long Done(int op)
            {
                long got = GuestMemory.Read<long>(param);
                Assert.Equal(got, (long)Call(io, "sceIoComplete", op));
                Assert.Equal(0x5A5A5A5Au, GuestMemory.R32(opt)); // the scratch struct is never written
                GuestMemory.Write(param, -1L);
                return got;
            }
            Assert.Equal(0L, Done(Call(io, "_sceIoMkdirAsync", "ux0:a", 0x1FF, param, opt)));
            Assert.True(Directory.Exists(Path.Combine(root, "d", "a")));
            Assert.Equal(0L, Done(Call(io, "_sceIoGetstatAsync", "ux0:a", stat, param, opt)));
            Assert.Equal(0L, Done(Call(io, "_sceIoRenameAsync", "ux0:a", "ux0:b", param, opt)));
            Assert.Equal(0L, Done(Call(io, "_sceIoRmdirAsync", "ux0:b", param, opt)));
            File.WriteAllText(Path.Combine(root, "d", "f"), "x");
            Assert.Equal(0L, Done(Call(io, "_sceIoRemoveAsync", "ux0:f", param, opt)));
            Assert.False(File.Exists(Path.Combine(root, "d", "f")));
            Assert.Equal((long)SceError.IO_ENOENT, Done(Call(io, "_sceIoRemoveAsync", "ux0:f", param, opt)));
            Done(Call(io, "_sceIoSyncAsync", 0u, 0, param, opt));
        }
        finally { try { Directory.Delete(root, true); } catch (IOException) { } }
    }

    [Fact]
    public void AsyncOpIsAnEventObject()
    {
        // libpaf waits for each sceIoReadAsync with sceKernelWaitEvent(opid, 1, ...) before sceIoComplete
        uint m = fx.Memory.Alloc(16, "evt");
        Ptr<uint> result = new(m), timeout = new(m + 4);
        Ptr<ulong> user = new(m + 8);
        var op = new IoAsyncOp { UserData = 0x1234 };
        Assert.Equal(SceError.EVENT_COND, op.PollEvent(1, result, user));
        timeout.Value = 1000;
        Assert.Equal(SceError.WAIT_TIMEOUT, op.WaitEvent(1, result, user, timeout));
        op.Finish(5);
        Assert.Equal(0, op.WaitEvent(1, result, user, new Ptr<uint>(0)));
        Assert.Equal(IoAsyncOp.CompletionBit, result.Value);
        Assert.Equal(0x1234ul, user.Value);
        Assert.Equal(0, op.PollEvent(1, result, user));
        Assert.Equal(SceError.INVALID_ARGUMENT, op.WaitEvent(0, result, user, timeout));
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

    [Fact]
    public void OpenOfAPathTheHostCannotRepresentFails()
    {
        // FFX HD's PhyreEngine FIOS layer probes "app0:app0:/Version_config/..." before "app0:/Version_config/...";
        // the second "app0:" is invalid syntax on Windows hosts and used to crash the thread with an IOException
        string root = Path.Combine(Path.GetTempPath(), "vitasharp-ioopen-" + Guid.NewGuid().ToString("N"));
        try
        {
            var emu = (VitaSharp.Core.Emulator)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(VitaSharp.Core.Emulator));
            void Set(string prop, object v) => typeof(VitaSharp.Core.Emulator).GetField($"<{prop}>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(emu, v);
            Set("Vfs", new VitaSharp.Core.Io.Vfs(root));
            Set("Hle", new VitaSharp.Core.Hle.HleRegistry(emu));
            emu.Vfs.Mount("app0", Path.Combine(root, "app"));
            Directory.CreateDirectory(Path.Combine(root, "app", "Version_config"));
            File.WriteAllText(Path.Combine(root, "app", "Version_config", "v.txt"), "1");
            var io = new IoFileMgr { Emu = emu };
            Assert.Equal(SceError.IO_ENOENT, Call(io, "sceIoOpen", "app0:app0:/Version_config/v.txt", 1, 0));
            int fd = Call(io, "sceIoOpen", "app0:/Version_config/v.txt", 1, 0);
            Assert.True(fd > 0);
            Assert.Equal(0, Call(io, "sceIoClose", fd));
        }
        finally
        {
            try { Directory.Delete(root, true); } catch (IOException) { }
        }
    }
}
