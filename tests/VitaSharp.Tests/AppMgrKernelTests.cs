using VitaSharp.Core.Hle;
using VitaSharp.Core.Hle.Modules;
using VitaSharp.Core.Kernel;
using VitaSharp.Core.Memory;
using Xunit;
using static VitaSharp.Tests.HleHarness;

namespace VitaSharp.Tests;

/// <summary>
/// SceAppMgrForDriver (SceAppMgr 3.74): every export has an entry; the kernel entries run the user and bridge HLE on
/// the same state (mounts, cloud data registration, local backups) with the kernel side's own checks (0x80800001,
/// 0x80800009, 0x80801001); process calls answer 0x80802002 for an unknown process.
/// </summary>
[Collection("guest")]
public sealed class AppMgrKernelTests(GuestMemoryFixture fixture) : IDisposable
{
    private const int Argument = unchecked((int)0x80800001), Caller = unchecked((int)0x80800009);
    private const int NoProcess = unchecked((int)0x80802002);

    private readonly HleHarness _harness = new(fixture, "appmgrk");

    public void Dispose() => _harness.Dispose();

    private VshBridge Bridge => _harness.Module<VshBridge>();

    private AppMgr Manager => _harness.Module<AppMgr>();

    private AppMgrShell Shell => _harness.Module<AppMgrShell>();

    [Fact]
    public void EveryExportHasAnEntry()
    {
        var bindings = HleBindings.Modules.SelectMany(m => m.Bindings()).Where(b => b.Library == "SceAppMgrForDriver").ToList();
        Assert.Equal(73, bindings.Select(b => b.Nid).Distinct().Count());
        Assert.Equal(bindings.Count, bindings.Select(b => b.Nid).Distinct().Count());
    }

    [Fact]
    public void TheCloudDataRegistrationIsTheBridges()
    {
        uint id = _harness.Alloc(8);
        GuestMemory.Write(id, 0x1122334455667788UL);
        Assert.Equal(0, Call(Bridge, "ksceAppMgrCloudDataSetMcId", 0, id));
        uint read = _harness.Alloc(8);
        Assert.Equal(0, Call(Bridge, "_vshAppMgrCloudDataGetMcId", 0, read));
        Assert.Equal(0x1122334455667788UL, GuestMemory.Read<ulong>(read));
        Assert.Equal(0, Call(Bridge, "vshAppMgrCloudDataClearMcId"));
        Assert.Equal(0, Call(Bridge, "ksceAppMgrCloudDataGetMcId", 0, read));
        Assert.Equal(0UL, GuestMemory.Read<ulong>(read));
    }

    /// <summary>NID_1574E1A4 (SceAppMgr 3.74 0x810232ec): the caller's right to the id first (0x80800009), by the
    /// caller's program authority ID for 0x6E-0x70, 0x12E-0x130, 500-505 and 900/901; only 900/901 mount (any other id
    /// 0x80800001), with the overlay's path in the id's folders (else 0x8080000C).</summary>
    [Fact]
    public void TheMountByIdChecksTheCallersRightThenTheSource()
    {
        uint result = _harness.Alloc(0x10);
        uint app = _harness.Str("ux0:/app/ABCD00001");
        uint save = _harness.Str("ux0:/user/00/savedata/ABCD00001");
        uint block = (uint)Invoke(_harness.Module<ProcessmgrKernel>(), "ksceKernelGetProcessKernelBuf", _harness.Emu.Pid)!;
        ulong authority = GuestMemory.Read<ulong>(block + 0xB8);
        int Mount(uint id, uint path, uint output)
        {
            int answer = 0;
            _harness.OnGuest(_ => answer = Call(Manager, "ksceAppMgr_1574E1A4", id, path, 0u, output));
            return answer;
        }

        try
        {
            // a game: the ids without a check pass to the id check, the others are refused
            Assert.Equal(Argument, Mount(0x100, app, result));
            Assert.Equal(Argument, Mount(0x6C, app, result));
            Assert.Equal(Caller, Mount(900, app, result));
            Assert.Equal(Caller, Mount(0x12E, app, result));
            Assert.Equal(Caller, Mount(502, app, result));
            Assert.Equal(Caller, Mount(0x6E, app, result));

            // the shell's authority: its ids pass and are no mount; 900/901 are not its
            GuestMemory.Write(block + 0xB8, 0x2800000000000001UL);
            Assert.Equal(Argument, Mount(0x12E, app, result));
            Assert.Equal(Argument, Mount(505, app, result));
            Assert.Equal(Argument, Mount(0x6E, app, result));
            Assert.Equal(Caller, Mount(900, app, result));
            GuestMemory.Write(block + 0xB8, 0x280000000000002DUL);
            Assert.Equal(Argument, Mount(0x6E, app, result));
            Assert.Equal(Caller, Mount(0x12E, app, result));

            // 0x2808000000000001 mounts by 900/901: NULL path or output, then the source folders
            GuestMemory.Write(block + 0xB8, 0x2808000000000001UL);
            Assert.Equal(Argument, Mount(900, 0u, result));
            Assert.Equal(Argument, Mount(901, save, 0u));
            Assert.Equal(unchecked((int)0x8080000C), Mount(900, _harness.Str("ux0:/data/ABCD00001"), result));
            Assert.Equal(unchecked((int)0x8080000C), Mount(901, app, result));
            Assert.Equal(unchecked((int)0x8080000C), Mount(900, save, result));
            Assert.NotEqual(unchecked((int)0x8080000C), Mount(900, app, result));
            Assert.NotEqual(unchecked((int)0x8080000C), Mount(900, _harness.Str("grw0:/patch/ABCD00001"), result));
            Assert.NotEqual(unchecked((int)0x8080000C), Mount(901, save, result));
            Assert.NotEqual(unchecked((int)0x8080000C), Mount(901, _harness.Str("ux0:/data/savedata/ABCD00001"), result));
            Assert.Equal(Caller, Mount(0x12E, app, result));
        }
        finally
        {
            GuestMemory.Write(block + 0xB8, authority);
        }
    }

    [Fact]
    public void TheKernelSideChecksItsArguments()
    {
        uint block = _harness.Alloc(0x200);
        Assert.Equal(Argument, Call(Bridge, "ksceAppMgrCheckPfsMounted", 0u));
        Assert.Equal(0, Call(Bridge, "ksceAppMgrCheckPfsMounted", _harness.Str("ux0:app/X")));
        Assert.Equal(Argument, Call(Bridge, "ksceAppMgrCheckPfsMounted", _harness.Str(new string('a', 0x124))));
        Assert.Equal(Argument, Call(Bridge, "ksceAppMgrCloudDataCreateHeader", block, 0x40000001u, block, block));
        Assert.Equal(0, Call(Bridge, "ksceAppMgrCloudDataCreateHeader", block, 0x100u, block + 0x20, block + 0x40));
        Assert.Equal(0, Call(Bridge, "ksceAppMgrCloudDataVerifyHeader", block + 0x40));
        Assert.Equal(Caller, Call(Bridge, "ksceAppMgrUpdateRifInfo", 0u));
        Assert.Equal(unchecked((int)0x80801001), Call(Bridge, "ksceAppMgrBgdlSetQueueStatus", 0u));
        Assert.Equal(unchecked((int)0x80802014), Call(Bridge, "ksceAppMgrCloudDataSrcMount", block, block, block));
        Assert.Equal(Argument, Call(Bridge, "ksceAppMgrCloudDataSrcMount", block, 0u, block));
        Assert.Equal(Argument, Call(Bridge, "ksceAppMgrRegisterPath", 0u, 0));
        Assert.Equal(Argument, Call(Manager, "ksceAppMgr_DCADB9FE", 0u));
        Assert.Equal(Argument, Call(Manager, "ksceAppMgr_0813AFCA", 1, block, 0x10u));
        Assert.Equal(0, Call(Manager, "ksceAppMgr_0813AFCA", 1, block, 0x20u));
    }

    /// <summary>ksceAppMgrLaunchAppByPath is a development kit's entry (SceAppMgr 3.74 asks
    /// ksceSblACMgrIsDevelopmentMode first): a retail unit answers 0x80020012 to every call, before any other check; with
    /// the development kit's dip switch (0x9F) the launch's own checks follow. It launched on a retail unit.</summary>
    [Fact]
    public void TheLaunchByPathIsADevelopmentKitsEntry()
    {
        uint block = _harness.Alloc(0x40);
        uint missing = _harness.Str("ux0:app/NONE00000/eboot.bin");
        GuestMemory.Fill(block, 0, 0x40);
        Assert.Equal(unchecked((int)0x80020012), Call(Manager, "ksceAppMgrLaunchAppByPath", 0u, 0u, 0u, 0u, 0u, 0u));
        Assert.Equal(unchecked((int)0x80020012), Call(Manager, "ksceAppMgrLaunchAppByPath", missing, 0u, 0u, 0u, block, 0u));
        SystemServicesVsh services = _harness.Module<SystemServicesVsh>();
        Invoke(services, "ksceKernelSetDipsw", 0x9Fu);
        try
        {
            Assert.Equal(unchecked((int)0x80802016), Call(Manager, "ksceAppMgrLaunchAppByPath", 0u, 0u, 0u, 0u, 0u, 0u));
            GuestMemory.W32(block + 4, 1);
            Assert.Equal(NoProcess, Call(Manager, "ksceAppMgrLaunchAppByPath", missing, 0u, 0u, 0u, block, 0u));
            GuestMemory.W32(block + 4, 0);
            Assert.Equal(unchecked((int)0x80800006), Call(Manager, "ksceAppMgrLaunchAppByPath", missing, 0u, 0u, 0u, block, 0u));
        }
        finally
        {
            Invoke(services, "ksceKernelClearDipsw", 0x9Fu);
        }
    }

    /// <summary>NID_C4924946 is a development kit's process launch (SceAppMgr 3.74 0x8100282c): a retail unit answers
    /// 0x80020012 to every call; with the development kit's dip switch (0x9F) the process manager creates the process
    /// with the arguments as given (the option is not copied: its size word reaches the creation as it is) and its
    /// errors come back. It answered 0x80020012 on a development kit too.</summary>
    [Fact]
    public void TheDevelopmentProcessLaunchGoesThroughTheProcessManager()
    {
        uint title = _harness.Str("PCSX00000");
        uint path = _harness.Str("ux0:app/PCSX00000/eboot.bin");
        uint option = _harness.Alloc(0x80);
        GuestMemory.Fill(option, 0, 0x80);
        GuestMemory.W32(option, 0x40);
        Assert.Equal(unchecked((int)0x80020012), Call(Shell, "ksceAppMgr_C4924946", title, 0x1000000u, path, 0u, 0u, option));
        SystemServicesVsh services = _harness.Module<SystemServicesVsh>();
        Invoke(services, "ksceKernelSetDipsw", 0x9Fu);
        try
        {
            Assert.Equal(SceError.MODULEMGR_NOENT, Call(Shell, "ksceAppMgr_C4924946", title, 0x1000000u, path, 0u, 0u, option));
            Assert.Equal(unchecked((int)0x80020005), Call(Shell, "ksceAppMgr_C4924946", 0u, 0x1000000u, path, 0u, 0u, option));
            Assert.Equal(unchecked((int)0x80029002), Call(Shell, "ksceAppMgr_C4924946", title, 0x3000000u, path, 0u, 0u, option));
            GuestMemory.W32(option, 0x80);
            Assert.Equal(unchecked((int)0x80020009), Call(Shell, "ksceAppMgr_C4924946", title, 0x1000000u, path, 0u, 0u, option));
        }
        finally
        {
            Invoke(services, "ksceKernelClearDipsw", 0x9Fu);
        }
    }

    /// <summary>The module's launches give the process manager the GXM debugging setting of the registry for the
    /// kernel's process (SceAppMgr 3.74 0x81011454): /DEVENV/TOOL vsh_uses_gxmdbg, not the game programs'
    /// game_uses_gxmdbg; 0 without the key (a retail unit's registry). NID_324DD34E passed 0 whatever the
    /// registry said.</summary>
    [Fact]
    public void TheLaunchesTakeTheKernelsGxmDebuggingSetting()
    {
        Assert.Equal(0u, AppMgr.GxmDebugging(_harness.Emu, KernelSpace.KernelPid));
        var registry = Core.Firmware.Registry.For(_harness.Emu.Options.FsRoot);
        registry.Define("/DEVENV/TOOL/vsh_uses_gxmdbg", Core.Firmware.Registry.KeyType.Int, 4);
        registry.Define("/DEVENV/TOOL/game_uses_gxmdbg", Core.Firmware.Registry.KeyType.Int, 4);
        Assert.True(registry.SetInt("/DEVENV/TOOL", "game_uses_gxmdbg", 1));
        Assert.Equal(0u, AppMgr.GxmDebugging(_harness.Emu, KernelSpace.KernelPid));
        Assert.True(registry.SetInt("/DEVENV/TOOL", "vsh_uses_gxmdbg", 1));
        Assert.Equal(1u, AppMgr.GxmDebugging(_harness.Emu, KernelSpace.KernelPid));
    }

    /// <summary>NID_324DD34E launches a process through the process manager (SceAppMgr 3.74 0x81002794): the option
    /// copied by its size word (none: a valid one of 0x40 bytes), the creation's errors come back. It answered
    /// 0x80800009 to every caller.</summary>
    [Fact]
    public void TheProcessLaunchGoesThroughTheProcessManager()
    {
        uint title = _harness.Str("PCSX00000");
        uint path = _harness.Str("ux0:app/PCSX00000/eboot.bin");
        uint option = _harness.Alloc(0x80);
        GuestMemory.Fill(option, 0, 0x80);
        Assert.Equal(SceError.MODULEMGR_NOENT, Call(Manager, "ksceAppMgr_324DD34E", title, 0x1000000u, path, 0u, 0u, 0u));
        Assert.Equal(SceError.MODULEMGR_NOENT, Call(Manager, "ksceAppMgr_324DD34E", title, 0x4000000u, path, 0u, 0u, 0u));
        Assert.Equal(unchecked((int)0x80020005), Call(Manager, "ksceAppMgr_324DD34E", 0u, 0x1000000u, path, 0u, 0u, 0u));
        Assert.Equal(unchecked((int)0x80029002), Call(Manager, "ksceAppMgr_324DD34E", title, 0x3000000u, path, 0u, 0u, 0u));
        GuestMemory.W32(option, 0x20);
        Assert.Equal(unchecked((int)0x80020009), Call(Manager, "ksceAppMgr_324DD34E", title, 0x1000000u, path, 0u, 0u, option));
        // a larger size is copied as 0x40 bytes and keeps its word: not a valid option
        GuestMemory.W32(option, 0x80);
        Assert.Equal(unchecked((int)0x80020009), Call(Manager, "ksceAppMgr_324DD34E", title, 0x1000000u, path, 0u, 0u, option));
        GuestMemory.W32(option, 0x40);
        Assert.Equal(SceError.MODULEMGR_NOENT, Call(Manager, "ksceAppMgr_324DD34E", title, 0x1000000u, path, 0u, 0u, option));
    }

    /// <summary>NID_E8DB7942 takes away a mount by its mount point (SceAppMgr 3.74 0x81018864): only for a caller
    /// with the kernel's program authority (an application's thread 0x80800009), NULL 0x80800001, a mount point no
    /// mount has 0x80800002, else the mount goes (unmounted, out of the list). It refused every caller.</summary>
    [Fact]
    public void AMountIsTakenAwayByItsMountPointFromTheKernel()
    {
        Directory.CreateDirectory(Path.Combine(_harness.Root, "ux0", "app", "QUICK0001"));
        uint mountPoint = _harness.Alloc(16);
        Assert.Equal(0, Call(Manager, "sceAppMgrGameDataMount", _harness.Str("ux0:app/QUICK0001"), 0u, 0u, mountPoint));
        string device = GuestMemory.ReadCString(mountPoint, 16);
        Assert.NotNull(_harness.Emu.Vfs.Resolve(device));
        uint unknown = _harness.Str("gd999:");
        _harness.OnGuest(t =>
        {
            Assert.Equal(Caller, Call(Manager, "ksceAppMgr_E8DB7942", mountPoint));
            t.KernelMode = true;
            try
            {
                Assert.Equal(Argument, Call(Manager, "ksceAppMgr_E8DB7942", 0u));
                Assert.Equal(unchecked((int)0x80800002), Call(Manager, "ksceAppMgr_E8DB7942", unknown));
                Assert.Equal(0, Call(Manager, "ksceAppMgr_E8DB7942", mountPoint));
                Assert.Equal(unchecked((int)0x80800002), Call(Manager, "ksceAppMgr_E8DB7942", mountPoint));
            }
            finally
            {
                t.KernelMode = false;
            }
        });
        Assert.Null(_harness.Emu.Vfs.Resolve(device));
        Assert.Equal(unchecked((int)0x80802012), Call(Manager, "sceAppMgrUmount", mountPoint));
    }

    /// <summary>NID_504023C8 is the kernel's general launch at a path (SceAppMgr 3.74 0x8100d25c), with no caller
    /// check of its own: a path that names no program 0x80800006 from any caller (the shell, an application, the
    /// kernel); an "app0:" path needs a calling application (0x8080201C from a kernel thread, 0x8080201E from the
    /// shell, whose entry in the application list is of type 2); an argument list that
    /// does not fit its 0x400-byte block 0x8001005B. It answered 0x8080201C to every caller but the shell, and 0x80800007
    /// to the shell.</summary>
    [Fact]
    public void TheGeneralLaunchTakesAPathAnArgumentListAndTheCallersApp0()
    {
        uint missing = _harness.Str("ux0:app/NONE00000/eboot.bin");
        uint inApp0 = _harness.Str("app0:sce_module/none.self");
        uint list = _harness.Alloc(16);
        GuestMemory.W32(list, _harness.Str("-a"));
        GuestMemory.W32(list + 4, _harness.Str("second"));
        GuestMemory.W32(list + 8, 0);
        Assert.Equal(unchecked((int)0x80802016), Call(Manager, "ksceAppMgr_504023C8", 0u, 0u, 0u, 0u, 0u, 0u, 0u, 0u));
        Assert.Equal(unchecked((int)0x80800006), Call(Manager, "ksceAppMgr_504023C8", missing, 0u, 0u, 0u, 0u, 0u, list, 0u));
        _harness.OnGuest(t =>
        {
            Assert.Equal(unchecked((int)0x80800006), Call(Manager, "ksceAppMgr_504023C8", inApp0, 0u, 0u, 0u, 0u, 0u, 0u, 0u));
            t.KernelMode = true;
            try
            {
                Assert.Equal(unchecked((int)0x8080201C), Call(Manager, "ksceAppMgr_504023C8", inApp0, 0u, 0u, 0u, 0u, 0u, 0u, 0u));
                Assert.Equal(unchecked((int)0x80800006), Call(Manager, "ksceAppMgr_504023C8", missing, 0u, 0u, 0u, 0u, 0u, 0u, 0u));
            }
            finally
            {
                t.KernelMode = false;
            }

            // the shell's entry is of type 2: no app0 to read the path in
            _harness.Emu.IsShell = true;
            try
            {
                Assert.Equal(unchecked((int)0x8080201E), Call(Manager, "ksceAppMgr_504023C8", inApp0, 0u, 0u, 0u, 0u, 0u, 0u, 0u));
                Assert.Equal(unchecked((int)0x80800006), Call(Manager, "ksceAppMgr_504023C8", missing, 0u, 0u, 0u, 0u, 0u, 0u, 0u));
            }
            finally
            {
                _harness.Emu.IsShell = false;
            }
        });

        // the argument list: the strings in order, each with its terminator, in one 0x400-byte block
        Assert.Equal(10, AppMgr.ReadArgumentList(list, out string[] arguments));
        Assert.Equal(["-a", "second"], arguments);
        Assert.Equal(0, AppMgr.ReadArgumentList(0, out string[] none));
        Assert.Empty(none);
        uint full = _harness.Alloc(16);
        GuestMemory.W32(full, _harness.Str(new string('x', 0x3FF)));
        GuestMemory.W32(full + 4, 0);
        Assert.Equal(0x400, AppMgr.ReadArgumentList(full, out _));
        GuestMemory.W32(full + 4, _harness.Str("y"));
        GuestMemory.W32(full + 8, 0);
        Assert.Equal(unchecked((int)0x8001005B), AppMgr.ReadArgumentList(full, out _));
        uint tooLong = _harness.Str(new string('x', 0x400));
        GuestMemory.W32(full, tooLong);
        Assert.Equal(unchecked((int)0x8001005B), AppMgr.ReadArgumentList(full, out _));
        GuestMemory.W32(list + 8, tooLong);
        GuestMemory.W32(list + 12, 0);
        Assert.Equal(unchecked((int)0x8001005B), Call(Manager, "ksceAppMgr_504023C8", missing, 0u, 0u, 0u, 0u, 0u, list, 0u));
    }

    [Fact]
    public void ProcessCallsNeedARunningApplication()
    {
        uint state = _harness.Alloc(4);
        Assert.Equal(NoProcess, Call(Shell, "ksceAppMgrKillProcess", 0x7FFF0001));
        Assert.Equal(unchecked((int)0x80802012), Call(Shell, "ksceAppMgrDestroyOtherAppByPid", 0x7FFF0001));
        Assert.Equal(NoProcess, Call(Shell, "ksceAppMgr_29421DCD", 0x7FFF0001));
        Assert.Equal(unchecked((int)0x80802016), Call(Shell, "ksceAppMgr_21BE167A", 0x7FFF0001, 0u));
        Assert.Equal(NoProcess, Call(Shell, "ksceAppMgr_21BE167A", 0x7FFF0001, state));
        // the kernel launch by name is the kernel process's (SceAppMgr 3.74 0x8100dc08)
        Assert.Equal(unchecked((int)0x8080201F), Call(Shell, "ksceAppMgr_99AA37C1", _harness.Str("")));
    }
}
