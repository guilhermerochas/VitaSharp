using System.Reflection;
using System.Runtime.CompilerServices;
using VitaSharp.Core;
using VitaSharp.Core.Hle;
using VitaSharp.Core.Hle.Modules;
using VitaSharp.Core.Io;
using VitaSharp.Core.Kernel;
using VitaSharp.Core.Loader;
using VitaSharp.Core.Memory;

namespace VitaSharp.Tests;

/// <summary>
/// Last open items of packages A, B, C, D and F: thread event info, process time syscalls, the I/O ForSystem and "2"
/// aliases, Razor/touch/audio-in FORA entries, the PSP save data dialog and sceAppUtilInit's FW 3.60 checks.
/// Guest-executing paths (sceKernelCallWithChangeStack switching stacks) were checked with a scratch harness.
/// </summary>
[Collection("guest")]
public sealed class HleRestTests(GuestMemoryFixture fx) : IDisposable
{
    readonly string _root = Path.Combine(Path.GetTempPath(), "vitasharp-hlerest-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch (IOException) { }
    }

    static object? Invoke(object module, string name, params object?[] args) =>
        module.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public)!.Invoke(module, args);

    static int Call(object module, string name, params object?[] args) => Convert.ToInt32(Invoke(module, name, args));

    uint Alloc(uint size) { uint p = fx.Memory.Alloc(size, "hlerest"); GuestMemory.Fill(p, 0, size); return p; }

    static void SetBacking(object o, string prop, object value) =>
        o.GetType().GetField($"<{prop}>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(o, value);

    /// <summary>An Emulator shell with options, VFS, a kernel object table and an (empty) linker.</summary>
    Emulator Shell()
    {
        var emu = (Emulator)RuntimeHelpers.GetUninitializedObject(typeof(Emulator));
        SetBacking(emu, "Options", new EmulatorOptions { FsRoot = _root });
        SetBacking(emu, "Vfs", new Vfs(_root));
        SetBacking(emu, "TitleId", "PCSA00001");
        SetBacking(emu, "Kernel", new VitaSharp.Core.Kernel.Kernel(emu));
        var hle = new HleRegistry(emu);
        SetBacking(emu, "Hle", hle);
        SetBacking(emu, "Linker", new Linker(hle));
        emu.Vfs.Mount("savedata0", Path.Combine(_root, "savedata"));
        return emu;
    }

    // ---------------------------------------------------------------- A: kernel

    [Fact]
    public void ThreadEventInfoLayout()
    {
        var emu = Shell();
        var tm = new ThreadMgr { Emu = emu };
        int uid = emu.Kernel.Objects.Register(new ThreadEventHandler { Name = "evh", ThreadMask = 0x10027, EventMask = 0xF, Handler = 0x81000001, Common = 0x1234 });
        uint info = Alloc(0x44);
        GuestMemory.W32(info, 0x40);
        Assert.Equal(0, Call(tm, "sceKernelGetThreadEventInfo", uid, info));
        Assert.Equal(0x40u, GuestMemory.R32(info));
        Assert.Equal((uint)uid, GuestMemory.R32(info + 4));
        Assert.Equal("evh", GuestMemory.ReadCString(info + 8, 32));
        Assert.Equal(0xFu, GuestMemory.R32(info + 0x2C));
        Assert.Equal(0x10027u, GuestMemory.R32(info + 0x30));
        Assert.Equal(0u, GuestMemory.R32(info + 0x34)); // thread group target
        Assert.Equal(0x81000001u, GuestMemory.R32(info + 0x38));
        Assert.Equal(0x1234u, GuestMemory.R32(info + 0x3C));
        GuestMemory.W32(info, 0x44);
        Assert.Equal(SceError.NO_MEMORY, Call(tm, "sceKernelGetThreadEventInfo", uid, info));
        GuestMemory.W32(info, 0x40);
        Assert.Equal(SceError.UNKNOWN_THREAD_EVENT_ID, Call(tm, "sceKernelGetThreadEventInfo", uid + 2, info));
        Assert.Equal(SceError.ILLEGAL_ADDR, Call(tm, "sceKernelGetThreadEventInfo", uid, 0u));
        // syscall form: the size comes from the 8-byte opt
        uint opt = Alloc(8);
        GuestMemory.W32(opt, 8);
        GuestMemory.W32(info + 8, 0xEEEEEEEE);
        Assert.Equal(0, Call(tm, "_sceKernelGetThreadEventInfo", uid, info, opt));
        Assert.Equal(0xEEEEEEEEu, GuestMemory.R32(info + 8)); // past the requested size: untouched
    }

    [Fact]
    public void CallWithChangeStackRejectsMissingStackAndProcessTimeCore()
    {
        var tm = new ThreadMgr { Emu = Shell() };
        Assert.Equal(SceError.ILLEGAL_ADDR, Call(tm, "sceKernelCallWithChangeStack", 0u, 0x1000u, 0x81000001u, 0u));
        Assert.Equal(SceError.ILLEGAL_ADDR, Call(tm, "sceKernelCallWithChangeStack", 0x81000000u, 4u, 0x81000001u, 0u));
        Assert.Equal(SceError.ILLEGAL_ADDR, Call(tm, "sceKernelGetProcessTimeCore", new Ptr<ulong>(0)));
        uint clk = Alloc(8);
        GuestMemory.Write(clk, ulong.MaxValue);
        Assert.Equal(0, Call(tm, "sceKernelGetProcessTimeCore", new Ptr<ulong>(clk)));
        Assert.NotEqual(ulong.MaxValue, GuestMemory.Read<ulong>(clk));
    }

    // ---------------------------------------------------------------- B: I/O

    [Fact]
    public void IoForSystemAliasesFlockAndCompleteMultiple()
    {
        var io = new IoFileMgr();
        Assert.Equal(SceError.IO_EBADF, Call(io, "sceIoFlockForSystem", 0x1234, 0));
        // CompleteMultiple: 0x18-byte entries starting with the opid; finishes and frees them, counts them
        int op1 = Call(io, "sceIoOpenAsync", "tty0:", 3, 0), op2 = Call(io, "sceIoOpenAsync", "tty1:", 3, 0);
        uint ps = Alloc(0x18 * 3), n = Alloc(4);
        GuestMemory.W32(ps, (uint)op1);
        GuestMemory.W32(ps + 0x18, (uint)op2);
        GuestMemory.W32(ps + 0x30, 0x7FFF0001); // unknown opid stops the walk
        Assert.Equal(SceError.UNKNOWN_EVENT_ID, Call(io, "sceIoCompleteMultiple", ps, 3, new Ptr<int>(n)));
        Assert.Equal(2u, GuestMemory.R32(n));
        Assert.Equal(SceError.UNKNOWN_EVENT_ID, Call(io, "sceIoComplete", op1)); // freed
        Assert.Equal(0, Call(io, "sceIoCompleteMultiple", ps, 0, new Ptr<int>(n)));
        Assert.Equal(unchecked((int)0x80010016), Call(io, "sceIoCompleteMultiple", ps, 0x5D, new Ptr<int>(n)));
        // the "2" exports of SceLibKernel are the SceIofilemgr syscalls (FW 3.60 export table), and the ForSystem
        // entries share the plain implementations
        var attrs = typeof(IoFileMgr).GetMethods(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
            .SelectMany(m => m.GetCustomAttributes<HleAttribute>().Select(a => (a.Name ?? m.Name, m.Name))).ToList();
        Assert.Contains(("sceIoRead2", "sceIoRead"), attrs);
        Assert.Contains(("sceIoWrite2", "sceIoWrite"), attrs);
        Assert.Contains(("sceIoClose2", "sceIoClose"), attrs);
        Assert.Contains(("sceIoGetPriorityForSystem", "sceIoGetPriority"), attrs);
        Assert.Contains(("sceIoSetThreadDefaultPriorityForSystem", "sceIoSetThreadDefaultPriority"), attrs);
    }

    // ---------------------------------------------------------------- C, F: FORA entries

    [Fact]
    public void RazorTouchAndAudioInForaEntries()
    {
        var gxm = new Gxm();
        Assert.Equal(0, Call(gxm, "sceGxmAddRazorGpuCaptureBuffer", 0x81000000u, 0x1000u));
        Assert.Equal(unchecked((int)0x805B0004), Call(gxm, "sceGxmAddRazorGpuCaptureBuffer", 0u, 0x1000u));
        Assert.Equal(0, Call(gxm, "sceGxmRemoveRazorGpuCaptureBuffer", 0x81000000u));
        Assert.Equal(0, Call(gxm, "sceGxmPadTriggerGpuPaTrace"));

        var touch = new Touch();
        const int PRIV = unchecked((int)0x80350002);
        Assert.Equal(PRIV, Call(touch, "sceTouchGetDeviceInfo", 0u, Alloc(0xC)));
        Assert.Equal(PRIV, Call(touch, "sceTouchGetProcessInfo", 0u, 0u));
        Assert.Equal(PRIV, Call(touch, "sceTouchSetProcessPrivilege", 0u, 0u));

        var audio = new Audio();
        int port = Call(audio, "sceAudioInOpenPortForDiag", 14, 0, 0, 0); // DIAG: grain/freq not validated
        Assert.True(port > 0);
        Assert.Equal(unchecked((int)0x80260107), Call(audio, "sceAudioInOpenPortForDiag", 14, 256, 16000, 0)); // one port
        Assert.Equal(0, Call(audio, "sceAudioInReleasePort", port));
        Assert.Equal(unchecked((int)0x80260103), Call(audio, "sceAudioInOpenPortForDiag", 0, 256, 44100, 0)); // other types as OpenPort
    }

    // ---------------------------------------------------------------- D: dialogs and AppUtil

    [Fact]
    public void PspSaveDataDialogIsACanceledCommonDialog()
    {
        var d = new CommonDialog();
        const int NULL = unchecked((int)0x80020402), BUSY = unchecked((int)0x80020401), NOT_IN_USE = unchecked((int)0x80020411);
        uint param = Alloc(0x100), result = Alloc(0x40);
        Assert.Equal(NOT_IN_USE, Call(d, "scePspSaveDataDialogContinue", param));
        Assert.Equal(NULL, Call(d, "scePspSaveDataDialogInit", 0u));
        Assert.Equal(0, Call(d, "scePspSaveDataDialogInit", param));
        Assert.Equal(BUSY, Call(d, "sceTwDialogInit", param)); // shares the common dialog slot
        Assert.Equal(0, Call(d, "scePspSaveDataDialogContinue", param));
        GuestMemory.W32(result + 4, 0xAAAAAAAA);
        Assert.Equal(0, Call(d, "scePspSaveDataDialogGetResult", result));
        Assert.Equal(1u, GuestMemory.R32(result));         // USER_CANCELED
        Assert.Equal(0xAAAAAAAAu, GuestMemory.R32(result + 4)); // layout past the result word unknown: untouched
        Assert.Equal(0, Call(d, "scePspSaveDataDialogTerm"));
        Assert.Equal(NOT_IN_USE, Call(d, "scePspSaveDataDialogTerm"));
    }

    [Fact]
    public void AppUtilInitChecksReservedBytesAndBusy()
    {
        const int PARAMETER = unchecked((int)0x80100600), NOT_INITIALIZED = unchecked((int)0x80100601), BUSY = unchecked((int)0x80100603);
        var emu = Shell();
        var util = new AppUtil { Emu = emu };
        uint init = Alloc(0x40), boot = Alloc(0x28);
        Assert.Equal(NOT_INITIALIZED, Call(util, "sceAppUtilShutdown"));
        Assert.Equal(PARAMETER, Call(util, "sceAppUtilInit", 0u, boot));
        Assert.Equal(PARAMETER, Call(util, "sceAppUtilInit", init, 0u));
        GuestMemory.W32(init, 0x1000); // workBufSize must be zero
        Assert.Equal(PARAMETER, Call(util, "sceAppUtilInit", init, boot));
        GuestMemory.W32(init, 0);
        GuestMemory.Write(boot + 0x27, (byte)1); // reserved
        Assert.Equal(PARAMETER, Call(util, "sceAppUtilInit", init, boot));
        GuestMemory.Write(boot + 0x27, (byte)0);
        GuestMemory.W32(boot, 0xFFFF);
        GuestMemory.W32(boot + 4, 0xFFFF);
        Assert.Equal(0, Call(util, "sceAppUtilInit", init, boot));
        Assert.Equal(0u, GuestMemory.R32(boot));     // attr
        Assert.Equal(0u, GuestMemory.R32(boot + 4)); // appVersion: FW 3.60 boot param words are zeroed
        // no process param (SDK 0): an old-SDK title may initialise twice
        Assert.Equal(0, Call(util, "sceAppUtilInit", init, boot));
        // SDK 3.60: BUSY
        uint pp = Alloc(0x40);
        GuestMemory.W32(pp, 0x40);
        GuestMemory.W32(pp + 4, 0x32505350);
        GuestMemory.W32(pp + 8, 6);
        GuestMemory.W32(pp + 0xC, 0x03600011);
        SetBacking(emu.Linker, "ProcessParam", pp);
        Assert.Equal(BUSY, Call(util, "sceAppUtilInit", init, boot));
        Assert.Equal(0, Call(util, "sceAppUtilShutdown"));
        Assert.Equal(0, Call(util, "sceAppUtilInit", init, boot));
    }
}
