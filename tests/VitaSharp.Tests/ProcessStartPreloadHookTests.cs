using VitaSharp.Core;
using VitaSharp.Core.Firmware;
using VitaSharp.Core.Hle.Modules;
using VitaSharp.Core.Kernel;
using VitaSharp.Core.Memory;
using Xunit;
using static VitaSharp.Tests.HleHarness;

namespace VitaSharp.Tests;

/// <summary>
/// processmgr starts a new process's preloaded modules through modulemgr's export ksceKernelStartPreloadingModules(pid)
/// (FW 3.74 processmgr.skprx, study only), so a kernel plugin's hook on that export runs for every process the shell
/// opens after the hook, with the new process's pid, before the process's main thread runs: what a homebrew
/// environment's PSP-emulator loader uses to add its user module to the emulator's process. Synthetic: the shell's
/// process and an application next to it (<see cref="SyntheticTitle"/>), a firmware module in os0:kd exporting
/// SceModulemgrForKernel, and a kernel plugin whose A32 hook function writes the pid it gets into its own data area.
/// </summary>
[Collection("guest")]
public sealed class ProcessStartPreloadHookTests : IDisposable
{
    /// <summary>The word after the plugin's hook function (its fifth A32 word), where the hook writes the pid.</summary>
    private const uint PluginDataOffset = 0xC10;

    private readonly HleHarness _harness;
    private readonly Emulator _shell;
    private readonly List<Emulator> _apps = [];

    public ProcessStartPreloadHookTests(GuestMemoryFixture fixture)
    {
        // the title's segment is fixed at the start of user memory: blocks other tests of the shared memory left there
        for (uint address = SyntheticTitle.Base; address < SyntheticTitle.Base + 0x0010_0000; address += 0x1000)
        {
            if (fixture.Memory.FindBlock(address) is { } leftover)
            {
                fixture.Memory.Free(leftover.Address);
            }
        }

        _harness = new HleHarness(fixture, "kpreload", kernelPlugins: true);
        _shell = _harness.Emu;
        var modulemgr = new SceModuleBuilder("SceKernelModulemgr");
        int body = modulemgr.ReturnConstant(0);
        modulemgr.Export("SceModulemgrForKernel", Emulator.ModulemgrForKernelLibNid, 0x0001, (Emulator.StartPreloadingModulesNid, body));
        string kd = Path.Combine(_harness.Root, "os0", "kd");
        Directory.CreateDirectory(kd);
        File.WriteAllBytes(Path.Combine(kd, "modulemgr.skprx"), modulemgr.Build());
        FirmwareKernelImage.Forget(_harness.Root);

        SceModuleBuilder plugin = new SceModuleBuilder("PreloadHookPlugin").Start(0);
        // the hook (the first A32 function, at +0xC00): add r1, pc, #8 (r1 = +0xC10); str r0, [r1]; mov r0, #0; bx lr; then the word it writes
        plugin.Arm(0xE28F1008, 0xE5810000, 0xE3A00000, 0xE12FFF1E, 0);
        string tai = Path.Combine(_harness.Root, "ur0", "tai");
        Directory.CreateDirectory(tai);
        File.WriteAllBytes(Path.Combine(tai, "preloadhook.skprx"), plugin.Build());
        File.WriteAllText(Path.Combine(tai, "config.txt"), "*KERNEL\nur0:tai/preloadhook.skprx\n");
        _shell.LoadTitle(SyntheticTitle.Install(_harness.Root, "VSTS00001"));
    }

    public void Dispose()
    {
        foreach (Emulator app in _apps)
        {
            app.Stop();
        }

        _shell.Stop();
        FirmwareKernelImage.Forget(_harness.Root);
        _harness.Dispose();
    }

    private Emulator Kernel => KernelSpace.ProcessOf(_shell, KernelSpace.KernelPid)!;

    private PluginFramework KernelFramework => Kernel.Hle.Modules.OfType<PluginFramework>().Single();

    private uint PluginBase => KernelModules(_shell).Values.Single(m => m.Name == "PreloadHookPlugin").Segments[0].Address;

    private uint HookFunction => PluginBase + 0xC00;

    private uint RecordedPid => GuestMemory.R32(PluginBase + PluginDataOffset);

    private uint KernelWord()
    {
        using (ProcessScope.Enter(KernelSpace.KernelPid))
        {
            uint word = _shell.Memory.Alloc(16, "test:hook ref", MemoryRegion.Kernel, 16);
            GuestMemory.W32(word, 0);
            return word;
        }
    }

    /// <summary>Opens the application and waits until its start (module_start functions, the preloading call, the main
    /// thread's start) is done.</summary>
    private Emulator OpenApp()
    {
        using var started = new ManualResetEventSlim();
        Emulator app = _shell.SpawnProcess(SyntheticTitle.Install(_harness.Root, "VSTS00002"), "eboot.bin", [], started: _ => started.Set());
        _apps.Add(app);
        Assert.True(started.Wait(TimeSpan.FromMinutes(2)), "the application did not start");
        return app;
    }

    [Fact]
    public void AKernelHookOnStartPreloadingModulesRunsForAProcessOpenedAfterItWithItsPid()
    {
        uint refVariable = KernelWord();
        int uid = Call(KernelFramework, "HookFunctionExportForKernel", KernelSpace.KernelPid, refVariable, _harness.Str("SceKernelModulemgr"),
            Emulator.ModulemgrForKernelLibNid, Emulator.StartPreloadingModulesNid, HookFunction);
        Assert.True(uid > 0, $"hook returned 0x{uid:X8}");
        Assert.True(Kernel.FirmwareKernel.IsPatched(Emulator.ModulemgrForKernelLibNid, Emulator.StartPreloadingModulesNid));
        Assert.Equal(0u, RecordedPid);

        Emulator app = OpenApp();
        Assert.Equal((uint)app.Pid, RecordedPid);

        // released: the next process starts without the hook (the HLE answers, nothing runs)
        Assert.Equal(0, Call(KernelFramework, "HookReleaseForKernel", uid, GuestMemory.R32(refVariable)));
        GuestMemory.W32(PluginBase + PluginDataOffset, 0);
        app.Terminate("closed by the test");
        OpenApp();
        Assert.Equal(0u, RecordedPid);
    }

    /// <summary>The shell starts the firmware's PSP emulator by path with the title of the bubble it opens
    /// (sceAppMgrLaunchAppByPath4, or the second stage of a launch an application asked for): the kernel knows the new
    /// process by that title (ksceKernelSysrootGetProcessTitleId, the plugin configuration's section), while its
    /// executable stays the emulator's application.</summary>
    [Fact]
    public void AShellLaunchGivesTheProcessTheTitleItNames()
    {
        SyntheticTitle.Install(_harness.Root, "VSTS00002");
        const string BubbleTitle = "VSTS00009";
        int result = Call(_shell.Hle.Modules.OfType<SystemSoftware>().Single(), "sceAppMgrLaunchAppByPath4", "ux0:app/VSTS00002/eboot.bin",
            _harness.Str(BubbleTitle), 0x2000000u, 0u, 0u, 0u);
        Assert.Equal(0, result);
        Emulator app = _shell.Vita.Processes.Single(p => p.TitleId == "VSTS00002");
        _apps.Add(app);
        Assert.Equal(BubbleTitle, app.ProcessTitleId);

        uint buffer = _harness.Alloc(0x20);
        Assert.Equal(0, Call(Kernel.Hle.Modules.OfType<SysmemKernel>().Single(), "ksceKernelSysrootGetProcessTitleId", app.Pid, buffer, 0x20u));
        Assert.Equal(BubbleTitle, GuestMemory.ReadCString(buffer));

        // the shell's second stage of a requested launch with another executable (the PSP emulator for a bubble of the
        // PSP type): the process is the requested title's
        const string RequestedTitle = "VSTS00008";
        string requestedDir = SyntheticTitle.Install(_harness.Root, RequestedTitle);
        PendingLaunch pending = _shell.Vita.RequestLaunchByName(_shell, RequestedTitle, requestedDir, 0x20000, null);
        Assert.Equal(0, Call(_shell.Hle.Modules.OfType<AppMgrShell>().Single(), "sceAppMgrLaunchAppByName2ndStage", pending.App.Uid,
            _harness.Str("ux0:app/VSTS00002/eboot.bin"), 0, 0, 0u));
        Emulator emulatorProcess = _shell.Vita.Processes.Single(p => p.ProcessTitleId == RequestedTitle);
        _apps.Add(emulatorProcess);
        Assert.Equal("VSTS00002", emulatorProcess.TitleId);

        // a launch naming the executable's own title keeps it
        Emulator own = _shell.SpawnProcess(SyntheticTitle.Install(_harness.Root, "VSTS00003"), "eboot.bin", [], processTitleId: "vsts00003");
        _apps.Add(own);
        Assert.Equal("VSTS00003", own.ProcessTitleId);
    }
}
