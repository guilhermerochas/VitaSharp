using System.Text;
using VitaSharp.Core.Hle.Modules;
using VitaSharp.Core.Kernel;
using VitaSharp.Core.Loader;
using VitaSharp.Core.Memory;
using Xunit;
using static VitaSharp.Tests.HleHarness;

namespace VitaSharp.Tests;

/// <summary>
/// A kernel plugin's hook on ksceVopLookup (fd_fix hooks it) sees the path walk of every open on the emulator's own
/// devices (ux0:, app0:...), as on the Vita: iofilemgr 3.74 walks each path element by element from the device's root
/// vnode with ksceVopLookup (0x81004bb8, read in the decompiled module, study only), the user's sceIoOpen syscall and
/// the kernel's ksceIoOpen alike. Before, only a guest file system's mount was walked: the opens of a title's files on
/// ux0/app0 went straight to the host files and the hook was never called. The firmware module is a synthetic os0:kd
/// module exporting the function; the hook is a jump written over its entry, to a host function that records the
/// element and answers (by default continuing to the original with the caller's arguments).
/// </summary>
[Collection("guest")]
public sealed class IoVopLookupHookTests(GuestMemoryFixture fixture) : IDisposable
{
    private const uint IofilemgrForDriver = 0x40FD29C7, VopLookup = 0xA5A6A55C;
    private const int NoEnt = unchecked((int)0x80010002), Access = unchecked((int)0x8001000D);
    private const int ORdOnly = 1, OWrOnly = 2, OCreat = 0x200;

    private readonly HleHarness _h = new(fixture, "vop-lookup-hook");
    private readonly List<string> _elements = [];

    /// <summary>The thread each hook call ran on: its name and whether it is a kernel thread.</summary>
    private readonly List<(string Name, bool Kernel)> _threads = [];
    private Func<uint, uint, uint, uint, int?>? _answer;

    public void Dispose()
    {
        Core.Firmware.FirmwareKernelImage.Forget(_h.Root);
        _h.Dispose();
    }

    private IoFileMgr Io => _h.Module<IoFileMgr>();

    private static string Element(uint vfsPath) => GuestMemory.ReadCString(GuestMemory.R32(vfsPath), (int)GuestMemory.R32(vfsPath + 4));

    private string Data(string relative) => Path.Combine(_h.Root, "ux0", "data", "hfn", relative);

    /// <summary>The synthetic SceIofilemgr in os0:kd, loaded as the firmware kernel; returns ksceVopLookup's entry.</summary>
    private uint LoadFirmwareIofilemgr()
    {
        var iofilemgr = new SceModuleBuilder("SceIofilemgr");
        int body = iofilemgr.ReturnConstant(0);
        iofilemgr.Export("SceIofilemgrForDriver", IofilemgrForDriver, 0x0001, (VopLookup, body));
        string kd = Path.Combine(_h.Root, "os0", "kd");
        Directory.CreateDirectory(kd);
        File.WriteAllBytes(Path.Combine(kd, "iofilemgr.skprx"), iofilemgr.Build());
        Core.Firmware.FirmwareKernelImage.Forget(_h.Root);
        Assert.True(_h.Emu.FirmwareKernel.Load() > 0);
        uint entry = _h.Emu.FirmwareKernel.EntryOf(IofilemgrForDriver, VopLookup);
        Assert.NotEqual(0u, entry);
        return entry;
    }

    /// <summary>Writes the hook over the entry: it records each element and answers with <see cref="_answer"/>, else
    /// continues to the original.</summary>
    private void Hook(uint entry)
    {
        uint hook = _h.Emu.Memory.Alloc(16, "vop lookup hook");
        Linker.WriteHleStub(hook, _h.Emu.Hle.RegisterHostFunction("voptest:lookup hook", context =>
        {
            uint vnode = context.Gpr(0);
            uint vpp = context.Gpr(1);
            uint path = context.Gpr(2);
            uint flags = context.Gpr(3);
            _elements.Add(Element(path));
            GuestThread? caller = GuestThread.Current;
            _threads.Add((caller?.Name ?? "host", caller?.KernelMode ?? false));
            int? answer = _answer?.Invoke(vnode, vpp, path, flags);
            context.Return(answer ?? Call(Io, "ksceVopLookup", vnode, vpp, path, flags));
        }), 0);
        _h.Emu.Code.Invalidate(hook, 16);
        Linker.WriteJumpStub(entry, hook);
        _h.Emu.InvalidateCode(entry, 16);
    }

    private void Files()
    {
        Directory.CreateDirectory(Data("sub"));
        File.WriteAllText(Data(Path.Combine("sub", "a.txt")), "file a");
        File.WriteAllText(Data(Path.Combine("sub", "b.txt")), "file b");
    }

    private string ReadAll(int fd)
    {
        uint buffer = _h.Alloc(64);
        int count = Io.sceIoRead(fd, buffer, 64);
        Assert.True(count >= 0, $"read 0x{count:X8}");
        return GuestMemory.ReadCString(buffer, count);
    }

    [Fact]
    public void EveryElementOfAnOpenOnTheDevicesGoesThroughTheHookedEntry()
    {
        Files();
        uint entry = LoadFirmwareIofilemgr();
        _h.OnGuest(_ =>
        {
            int unhooked = Io.sceIoOpen("ux0:data/hfn/sub/a.txt", ORdOnly, 0);
            Assert.True(unhooked > 0, $"open 0x{unhooked:X8}");
            Assert.Equal(0, Io.sceIoClose(unhooked));
            Assert.Empty(_elements);

            Hook(entry);
            // the user's syscall (sceIoOpen of SceLibKernel ends in it) and the kernel's ksceIoOpen
            int fd = Call(Io, "_sceIoOpen", "ux0:data/hfn/sub/a.txt", ORdOnly, 0, 0u);
            Assert.True(fd > 0, $"open through the hook 0x{fd:X8}");
            Assert.Equal(["data", "hfn", "sub", "a.txt"], _elements);
            Assert.Equal("file a", ReadAll(fd));
            Assert.Equal(0, Io.sceIoClose(fd));

            _elements.Clear();
            int kernel = Call(Io, "ksceIoOpen", "ux0:/data//hfn/sub/b.txt", ORdOnly, 0);
            Assert.True(kernel > 0, $"ksceIoOpen 0x{kernel:X8}");
            Assert.Equal(["data", "hfn", "sub", "b.txt"], _elements);
            Assert.Equal(0, Io.sceIoClose(kernel));

            // a missing file: the walk stops at the element that is not there, the open says so
            _elements.Clear();
            Assert.Equal(NoEnt, Io.sceIoOpen("ux0:data/hfn/none/a.txt", ORdOnly, 0));
            Assert.Equal(["data", "hfn", "none"], _elements);

            // a directory open and a stat walk the path too
            _elements.Clear();
            int dir = Io.sceIoDopen("ux0:data/hfn/sub");
            Assert.True(dir > 0, $"dopen 0x{dir:X8}");
            Assert.Equal(0, Io.sceIoDclose(dir));
            Assert.Equal(["data", "hfn", "sub"], _elements);
            _elements.Clear();
            uint stat = _h.Alloc(0x58);
            Assert.Equal(0, Io.sceIoGetstat("ux0:data/hfn/sub/b.txt", stat));
            Assert.Equal(6L, GuestMemory.Read<long>(stat + 8));
            Assert.Equal(["data", "hfn", "sub", "b.txt"], _elements);
        });
    }

    [Fact]
    public void AnOpenThatCreatesStillCreatesAfterTheLastLookupFindsNothing()
    {
        Files();
        Hook(LoadFirmwareIofilemgr());
        _h.OnGuest(_ =>
        {
            int fd = Io.sceIoOpen("ux0:data/hfn/sub/new.txt", OWrOnly | OCreat, 0x1B6);
            Assert.True(fd > 0, $"create 0x{fd:X8}");
            Assert.Equal(3, Io.sceIoWrite(fd, _h.Str("new"), 3));
            Assert.Equal(0, Io.sceIoClose(fd));
            Assert.Equal(["data", "hfn", "sub", "new.txt"], _elements);
            Assert.Equal("new", File.ReadAllText(Data(Path.Combine("sub", "new.txt"))));
        });
    }

    [Fact]
    public void WhatTheHookAnswersIsWhatTheOpenGets()
    {
        Files();
        Hook(LoadFirmwareIofilemgr());
        _h.OnGuest(_ =>
        {
            // an error the hook answers fails the open with it, a "not found" too for a file that is there
            _answer = (_, _, path, _) => Element(path) == "a.txt" ? Access : null;
            Assert.Equal(Access, Io.sceIoOpen("ux0:data/hfn/sub/a.txt", ORdOnly, 0));
            _answer = (_, _, path, _) => Element(path) == "a.txt" ? NoEnt : null;
            Assert.Equal(NoEnt, Io.sceIoOpen("ux0:data/hfn/sub/a.txt", ORdOnly, 0));
            Assert.Equal(NoEnt, Io.sceIoGetstat("ux0:data/hfn/sub/a.txt", _h.Alloc(0x58)));

            // a hook that looks up another element in the same folder (its own SceVfsPath) opens that file instead
            uint other = _h.Str("b.txt");
            uint otherPath = _h.Alloc(12);
            GuestMemory.W32(otherPath, other);
            GuestMemory.W32(otherPath + 4, 5);
            GuestMemory.W32(otherPath + 8, other);
            _answer = (vnode, vpp, path, flags) => Element(path) == "a.txt" ? Call(Io, "ksceVopLookup", vnode, vpp, otherPath, flags) : null;
            int fd = Io.sceIoOpen("ux0:data/hfn/sub/a.txt", ORdOnly, 0);
            Assert.True(fd > 0, $"redirected open 0x{fd:X8}");
            Assert.Equal("file b", ReadAll(fd));
            Assert.Equal(0, Io.sceIoClose(fd));
            _answer = null;
        });
        Assert.Equal(Encoding.ASCII.GetBytes("file a"), File.ReadAllBytes(Data(Path.Combine("sub", "a.txt"))));
    }

    /// <summary>
    /// An asynchronous open, directory open and stat walk the path too: on the Vita the request runs on iofilemgr's
    /// kernel thread (SceIoScheduler), which walks the vfs as the synchronous call does. Before, the I/O scheduler ran
    /// them on a host thread and the hook was never called. The hook runs on a kernel thread named as the firmware's,
    /// not on the submitting thread, and the request's result is what the walk and the call answer.
    /// </summary>
    [Fact]
    public void AnAsynchronousRequestWalksItsPathThroughTheHookOnAKernelThread()
    {
        Files();
        Hook(LoadFirmwareIofilemgr());
        uint param = _h.Alloc(0x18);
        uint stat = _h.Alloc(0x58);
        int open = 0;
        int dopen = 0;
        int getstat = 0;
        _h.OnGuest(_ =>
        {
            open = Call(Io, "sceIoOpenAsync", "ux0:data/hfn/sub/a.txt", ORdOnly, 0, param);
            dopen = Call(Io, "sceIoDopenAsync", "ux0:data/hfn/sub", 0u);
            getstat = Call(Io, "sceIoGetstatAsync", "ux0:data/hfn/sub/b.txt", stat, 0u);
        }, "io-async-submitter");
        Assert.True(open > 0 && dopen > 0 && getstat > 0, $"submit 0x{open:X8} 0x{dopen:X8} 0x{getstat:X8}");
        Io.WaitForAsyncHostWork();

        Assert.Equal(["data", "hfn", "sub", "a.txt", "data", "hfn", "sub", "data", "hfn", "sub", "b.txt"], _elements);
        Assert.All(_threads, thread => Assert.Equal(("SceIoScheduler", true), thread));
        int fd = 0;
        int dir = 0;
        int statResult = -1;
        _h.OnGuest(_ =>
        {
            fd = Call(Io, "sceIoComplete", open);
            dir = Call(Io, "sceIoComplete", dopen);
            statResult = Call(Io, "sceIoComplete", getstat);
        });
        Assert.True(fd > 0, $"async open 0x{fd:X8}");
        Assert.Equal(fd, (int)GuestMemory.Read<long>(param));
        Assert.True(dir > 0, $"async dopen 0x{dir:X8}");
        Assert.Equal(0, statResult);
        Assert.Equal(6L, GuestMemory.Read<long>(stat + 8));
        _h.OnGuest(_ =>
        {
            Assert.Equal("file a", ReadAll(fd));
            Assert.Equal(0, Io.sceIoClose(fd));
            Assert.Equal(0, Io.sceIoDclose(dir));
        });
    }

    /// <summary>What the hook answers on the request's kernel thread is the request's result: an error fails the
    /// asynchronous open with it (sceIoComplete and the SceIoAsyncParam), as it fails the synchronous one.</summary>
    [Fact]
    public void AnErrorTheHookAnswersIsTheAsynchronousRequestsResult()
    {
        Files();
        Hook(LoadFirmwareIofilemgr());
        _answer = (_, _, path, _) => Element(path) == "a.txt" ? Access : null;
        uint param = _h.Alloc(0x18);
        int open = 0;
        _h.OnGuest(_ => open = Call(Io, "sceIoOpenAsync", "ux0:data/hfn/sub/a.txt", ORdOnly, 0, param));
        Assert.True(open > 0, $"submit 0x{open:X8}");
        Io.WaitForAsyncHostWork();

        int result = 0;
        _h.OnGuest(_ => result = Call(Io, "sceIoComplete", open));
        Assert.Equal(Access, result);
        Assert.Equal(Access, (int)GuestMemory.Read<long>(param));
        Assert.Equal(["data", "hfn", "sub", "a.txt"], _elements);
        _answer = null;
    }

    /// <summary>Without a hook an asynchronous request walks nothing and no kernel thread is made for it.</summary>
    [Fact]
    public void WithoutAHookAnAsynchronousOpenWalksNothing()
    {
        Files();
        LoadFirmwareIofilemgr();
        uint param = _h.Alloc(0x18);
        int open = 0;
        _h.OnGuest(_ => open = Call(Io, "sceIoOpenAsync", "ux0:data/hfn/sub/a.txt", ORdOnly, 0, param));
        Io.WaitForAsyncHostWork();
        int fd = 0;
        _h.OnGuest(_ =>
        {
            fd = Call(Io, "sceIoComplete", open);
            Assert.Equal(0, Io.sceIoClose(fd));
        });
        Assert.True(fd > 0, $"async open 0x{fd:X8}");
        Assert.Empty(_elements);
    }

    /// <summary>
    /// The other calls that name a path walk it too, as iofilemgr 3.74 walks it for each of them (study only: the
    /// remove 0x81007f14, mkdir 0x810090b4, rmdir 0x810066cc, chstat 0x8100694c and rename 0x81009278, which walks the
    /// old path then the new one; each ends with the lookup of the last element): the user's syscalls (and the kernel's
    /// ksceIo* forms, which share their bodies). The element a mkdir or a rename's new path makes is looked up and not
    /// found, and the call still makes it. Before, these went straight to the host files and the hook was never called.
    /// </summary>
    [Fact]
    public void RemoveMkdirRmdirRenameAndChstatWalkTheirPathsThroughTheHook()
    {
        Files();
        Hook(LoadFirmwareIofilemgr());
        uint stat = _h.Alloc(0x58);
        _h.OnGuest(_ =>
        {
            Assert.Equal(0, Call(Io, "_sceIoMkdir", "ux0:data/hfn/made", 0x1FF, 0u));
            Assert.Equal(["data", "hfn", "made"], _elements);
            Assert.True(Directory.Exists(Data("made")));

            _elements.Clear();
            Assert.Equal(0, Call(Io, "_sceIoRename", "ux0:data/hfn/sub/b.txt", "ux0:data/hfn/made/c.txt", 0u));
            Assert.Equal(["data", "hfn", "sub", "b.txt", "data", "hfn", "made", "c.txt"], _elements);
            Assert.Equal("file b", File.ReadAllText(Data(Path.Combine("made", "c.txt"))));

            _elements.Clear();
            GuestMemory.Fill(stat, 0, 0x58);
            Assert.Equal(0, Call(Io, "_sceIoChstat", "ux0:data/hfn/made/c.txt", stat, 0, 0u));
            Assert.Equal(["data", "hfn", "made", "c.txt"], _elements);

            _elements.Clear();
            Assert.Equal(0, Call(Io, "_sceIoRemove", "ux0:data/hfn/made/c.txt", 0u));
            Assert.Equal(["data", "hfn", "made", "c.txt"], _elements);
            Assert.False(File.Exists(Data(Path.Combine("made", "c.txt"))));

            _elements.Clear();
            Assert.Equal(0, Call(Io, "_sceIoRmdir", "ux0:data/hfn/made", 0u));
            Assert.Equal(["data", "hfn", "made"], _elements);
            Assert.False(Directory.Exists(Data("made")));

            // a missing file: the walk stops where the path ends on the device, the call answers itself
            _elements.Clear();
            Assert.Equal(NoEnt, Call(Io, "_sceIoRemove", "ux0:data/hfn/none/x.txt", 0u));
            Assert.Equal(["data", "hfn", "none"], _elements);
        });
    }

    /// <summary>What the hook answers is what these calls get: an error fails a remove, a rename (on either path), a
    /// mkdir and a chstat with it and changes nothing; a remove the hook sends to another file removes that one.</summary>
    [Fact]
    public void WhatTheHookAnswersIsWhatAPathCallGets()
    {
        Files();
        Hook(LoadFirmwareIofilemgr());
        _h.OnGuest(_ =>
        {
            _answer = (_, _, path, _) => Element(path) is "a.txt" or "made" ? Access : null;
            Assert.Equal(Access, Io.sceIoRemove("ux0:data/hfn/sub/a.txt"));
            Assert.Equal(Access, Io.sceIoRename("ux0:data/hfn/sub/a.txt", "ux0:data/hfn/sub/d.txt"));
            Assert.Equal(Access, Io.sceIoRename("ux0:data/hfn/sub/b.txt", "ux0:data/hfn/made"));
            Assert.Equal(Access, Io.sceIoMkdir("ux0:data/hfn/made", 0x1FF));
            Assert.Equal(Access, Io.sceIoChstat("ux0:data/hfn/sub/a.txt", _h.Alloc(0x58), 0));
            Assert.True(File.Exists(Data(Path.Combine("sub", "a.txt"))));
            Assert.True(File.Exists(Data(Path.Combine("sub", "b.txt"))));
            Assert.False(Directory.Exists(Data("made")));

            // the remove's last lookup answered with b.txt's vnode removes b.txt
            uint other = _h.Str("b.txt");
            uint otherPath = _h.Alloc(12);
            GuestMemory.W32(otherPath, other);
            GuestMemory.W32(otherPath + 4, 5);
            GuestMemory.W32(otherPath + 8, other);
            _answer = (vnode, vpp, path, flags) => Element(path) == "a.txt" ? Call(Io, "ksceVopLookup", vnode, vpp, otherPath, flags) : null;
            Assert.Equal(0, Io.sceIoRemove("ux0:data/hfn/sub/a.txt"));
            Assert.True(File.Exists(Data(Path.Combine("sub", "a.txt"))));
            Assert.False(File.Exists(Data(Path.Combine("sub", "b.txt"))));
            _answer = null;
        });
    }

    /// <summary>The asynchronous forms run the same walk on the I/O scheduler's kernel thread.</summary>
    [Fact]
    public void AnAsynchronousRemoveWalksItsPathOnTheSchedulersKernelThread()
    {
        Files();
        Hook(LoadFirmwareIofilemgr());
        int remove = 0;
        _h.OnGuest(_ => remove = Call(Io, "sceIoRemoveAsync", "ux0:data/hfn/sub/a.txt", 0u));
        Io.WaitForAsyncHostWork();
        int result = -1;
        _h.OnGuest(_ => result = Call(Io, "sceIoComplete", remove));
        Assert.Equal(0, result);
        Assert.Equal(["data", "hfn", "sub", "a.txt"], _elements);
        Assert.All(_threads, thread => Assert.Equal(("SceIoScheduler", true), thread));
        Assert.False(File.Exists(Data(Path.Combine("sub", "a.txt"))));
    }
}
