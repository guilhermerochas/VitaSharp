using System.Text;
using VitaSharp.Core.Hle.Modules;
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
}
