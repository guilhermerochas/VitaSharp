using VitaSharp.Core;
using VitaSharp.Core.Kernel;
using VitaSharp.Core.Loader;
using VitaSharp.Core.Memory;
using VitaSharp.Core.SaveState;

namespace VitaSharp.Tests;

/// <summary>
/// A save state brought up on a computer that does not have the game (the re-run of a "Gravar tudo" capture someone
/// else recorded): <see cref="SaveStates.BootFromState"/> rebuilds the program's modules from the state's memory and
/// <see cref="SaveStates.Load"/> brings its threads back, with no file of the game read. Synthetic: a
/// <see cref="SyntheticTitle"/> runs with a counting thread, is saved, and the data folder with the title is deleted;
/// a new emulator on an empty data folder boots the state, and the program runs on from the saved values.
/// </summary>
[Collection("guest")]
public sealed class SaveStateBootFromStateTests
{
    private const uint DelayThread = 0x4B675D05;
    private const uint MarkerAddress = SyntheticTitle.Base + 0xF00;

    private readonly GuestMemoryFixture _fixture;

    public SaveStateBootFromStateTests(GuestMemoryFixture fixture)
    {
        _fixture = fixture;
        // the title's segment is fixed at the start of user memory: blocks other tests of the shared memory left there
        for (uint address = SyntheticTitle.Base; address < SyntheticTitle.Base + 0x0010_0000; address += 0x1000)
        {
            if (fixture.Memory.FindBlock(address) is { } leftover)
            {
                fixture.Memory.Free(leftover.Address);
            }
        }
    }

    /// <summary>A thread of <paramref name="process"/> that counts in its memory and sleeps between counts (A32 loop:
    /// count, sceKernelDelayThread, again); returns the counter's address.</summary>
    internal static uint StartCounter(Emulator process, string name)
    {
        uint counter, code;
        using (ProcessScope.Enter(process))
        {
            counter = process.Memory.Alloc(0x10, "test:counter");
            code = process.Memory.Alloc(0x40, "test:counter code");
            GuestMemory.Fill(counter, 0, 0x10);
            uint stub = code + 0x30;
            Linker.WriteHleStub(stub, process.Hle.GetHleFunctionIndex(DelayThread, 0, "SceThreadmgr"), DelayThread);
            uint[] words =
            [
                0xE59F4018, // ldr r4, [pc, #24]   (the counter)
                0xE5945000, // ldr r5, [r4]
                0xE2855001, // add r5, r5, #1
                0xE5845000, // str r5, [r4]
                0xE3A00E3E, // mov r0, #0x3E0      (992 us)
                0xE59FC008, // ldr r12, [pc, #8]   (the delay stub)
                0xE12FFF3C, // blx r12
                0xEAFFFFF7, // b to the start
                counter,
                stub,
            ];
            for (int i = 0; i < words.Length; i++)
            {
                GuestMemory.W32(code + (uint)i * 4, words[i]);
            }

            GuestThread thread = process.Kernel.CreateThread(name, code, 0x40, 0x4000, 0, 0x70000);
            thread.Start(0, 0);
        }

        return counter;
    }

    internal static uint Read(Emulator process, uint address)
    {
        using (AddressSpace.Use(process.Space))
        {
            return GuestMemory.R32(address);
        }
    }

    [Fact]
    public void AStateBootsAndRunsOnInADataFolderWithoutTheGame()
    {
        string statePath = Path.Combine(Path.GetTempPath(), $"vitasharp-bootstate-{Guid.NewGuid():N}.vss");
        uint counter;
        uint saved, before;
        string[] modules;
        try
        {
            using (var recorded = new HleHarness(_fixture, "bootstate-rec"))
            {
                Emulator emu = recorded.Emu;
                emu.LoadTitle(SyntheticTitle.Install(recorded.Root, "VSTS00001"));
                emu.Start();
                counter = StartCounter(emu, "VsTestBootCounter");
                using (AddressSpace.Use(emu.Space))
                {
                    GuestMemory.W32(MarkerAddress, 0xB007_5747);
                }

                HleHarness.Until(() => Read(emu, counter) > 5, "the first counts");
                before = Read(emu, counter);
                SaveStates.Save(emu, statePath, timeoutMs: 60_000);
                saved = Read(emu, counter);
                modules = emu.Kernel.Modules.Values.OrderBy(m => m.Uid).Select(m => $"{m.Name} {m.Path} {m.Segments[0].Address:X8}").ToArray();
                Assert.True(SaveStates.CanBootFromState(statePath));
                emu.Stop();
            }

            // the recording computer's data folder (and the game in it) is gone; this one never had the game
            using var analyst = new HleHarness(_fixture, "bootstate-run");
            Emulator again = analyst.Emu;
            Assert.False(Directory.Exists(Path.Combine(analyst.Root, "ux0", "app", "VSTS00001")));
            // a load needs a running program: without the game there was none to load into
            Assert.ThrowsAny<SnapshotException>(() => SaveStates.Load(again, statePath));
            SaveStates.BootFromState(again, statePath);
            Assert.Equal("VSTS00001", again.TitleId);
            Assert.Equal(SyntheticTitle.Title, again.Title);
            Assert.Equal("01.00", again.ParamSfo?.AppVersion);
            Assert.Equal("VitaSharpSynthetic", again.MainModule?.Name);
            Assert.Equal(modules, again.Kernel.Modules.Values.OrderBy(m => m.Uid).Select(m => $"{m.Name} {m.Path} {m.Segments[0].Address:X8}").ToArray());
            Assert.False(Directory.Exists(again.AppPath), "app0 is this data folder's (empty) application folder");

            SaveStates.Load(again, statePath);
            Assert.Equal(0xB007_5747u, Read(again, MarkerAddress));
            uint loaded = Read(again, counter);
            // the state holds the count of the moment of the save: between the reads before and after it
            Assert.True(loaded >= before && loaded <= saved, $"the counter is the saved one ({loaded}; {before} before the save, {saved} after it)");
            Assert.Single(again.Kernel.Threads, t => t.Name == "VsTestBootCounter");
            HleHarness.Until(() => Read(again, counter) > loaded + 5, "counts after the boot from the state");
            // the program's own main thread runs too (its frame loop presents)
            long frames = Core.Hle.Modules.Display.FramesPresented;
            HleHarness.Until(() => Core.Hle.Modules.Display.FramesPresented > frames + 2, "the title's frame loop after the boot");
            again.Stop();
        }
        finally
        {
            File.Delete(statePath);
        }
    }

    /// <summary>A state of several processes (the shell and an application it opened) boots where neither program is
    /// installed: the application process comes back next to the first one with the pid, application id and title it
    /// had, its modules rebuilt from its own address space, and after the load both processes' threads count on from
    /// the saved values. Before, the boot refused every state of more than one process.</summary>
    [Fact]
    public void AStateOfTheShellAndAnApplicationBootsWithoutTheirPrograms()
    {
        string statePath = Path.Combine(Path.GetTempPath(), $"vitasharp-bootstate-{Guid.NewGuid():N}.vss");
        uint shellCounter, appCounter;
        int appPid, appAppId;
        string[] appModules;
        try
        {
            using (var recorded = new HleHarness(_fixture, "bootstate-multirec"))
            {
                Emulator shell = recorded.Emu;
                shell.LoadTitle(SyntheticTitle.Install(recorded.Root, "VSTS00001"));
                shell.Start();
                using var started = new ManualResetEventSlim();
                Emulator app = shell.SpawnProcess(SyntheticTitle.Install(recorded.Root, "VSTS00002"), "eboot.bin", [], started: _ => started.Set());
                Assert.True(started.Wait(60_000), "the application did not start");
                HleHarness.Until(() => app.MainModule != null && app.Kernel.Threads.Any(), "the application's main thread");
                shellCounter = StartCounter(shell, "VsTestBootShellCounter");
                appCounter = StartCounter(app, "VsTestBootAppCounter");
                using (AddressSpace.Use(app.Space))
                {
                    GuestMemory.W32(MarkerAddress, 0xA990_0002);
                }

                HleHarness.Until(() => Read(shell, shellCounter) > 5 && Read(app, appCounter) > 5, "the first counts of both threads");
                SaveStates.Save(shell, statePath, timeoutMs: 60_000);
                appPid = app.Pid;
                appAppId = app.AppId;
                appModules = app.Kernel.Modules.Values.OrderBy(m => m.Uid).Select(m => $"{m.Name} {m.Path} {m.Segments[0].Address:X8}").ToArray();
                app.Stop();
                shell.Stop();
            }

            using var analyst = new HleHarness(_fixture, "bootstate-multirun");
            Emulator again = analyst.Emu;
            Assert.False(Directory.Exists(Path.Combine(analyst.Root, "ux0", "app", "VSTS00002")));
            SaveStates.BootFromState(again, statePath);
            Assert.Equal(2, again.Vita.Processes.Count);
            Emulator restoredApp = Assert.Single(again.Vita.Processes, p => !ReferenceEquals(p, again));
            Assert.Equal(appPid, restoredApp.Pid);
            Assert.Equal(appAppId, restoredApp.AppId);
            Assert.Equal("VSTS00002", restoredApp.TitleId);
            Assert.Equal("VitaSharpSynthetic", restoredApp.MainModule?.Name);
            Assert.Equal(appModules, restoredApp.Kernel.Modules.Values.OrderBy(m => m.Uid).Select(m => $"{m.Name} {m.Path} {m.Segments[0].Address:X8}").ToArray());
            Assert.NotSame(again.Space, restoredApp.Space);

            SaveStates.Load(again, statePath);
            Assert.Equal(0xA990_0002u, Read(restoredApp, MarkerAddress));
            uint shellLoaded = Read(again, shellCounter);
            uint appLoaded = Read(restoredApp, appCounter);
            Assert.Single(again.Kernel.Threads, t => t.Name == "VsTestBootShellCounter");
            Assert.Single(restoredApp.Kernel.Threads, t => t.Name == "VsTestBootAppCounter");
            HleHarness.Until(() => Read(again, shellCounter) > shellLoaded + 5 && Read(restoredApp, appCounter) > appLoaded + 5,
                "counts of both processes after the boot from the state");
            restoredApp.Stop();
            again.Stop();
        }
        finally
        {
            File.Delete(statePath);
        }
    }

    /// <summary>A state saved with a kernel plugin loaded (the recording user's ur0:tai) boots where the plugin is
    /// not installed and the plugin option is off: the plugin module comes back from the state's memory, in the kernel
    /// process, at its address, with its export.</summary>
    [Fact]
    public void AKernelPluginComesBackFromTheStateWithoutItsFile()
    {
        const uint TargetLib = 0x0BAD7A62, TargetNid = 0x2468ACE2;
        string statePath = Path.Combine(Path.GetTempPath(), $"vitasharp-bootstate-{Guid.NewGuid():N}.vss");
        uint pluginSegment;
        try
        {
            using (var recorded = new HleHarness(_fixture, "bootstate-plugrec", kernelPlugins: true))
            {
                Emulator emu = recorded.Emu;
                emu.LoadTitle(SyntheticTitle.Install(recorded.Root, "VSTS00001"));
                emu.Start();
                var builder = new SceModuleBuilder("BootStatePlugin");
                int returns7 = builder.ReturnConstant(7);
                builder.Export("BootStatePluginLib", TargetLib, 0x0001, (TargetNid, returns7));
                string dir = Path.Combine(recorded.Root, "ur0", "tai");
                Directory.CreateDirectory(dir);
                File.WriteAllBytes(Path.Combine(dir, "bootplugin.skprx"), builder.Build());
                LoadedModule plugin = emu.LoadModule("ur0:tai/bootplugin.skprx", kernel: true);
                pluginSegment = plugin.Segments[0].Address;
                uint counter = StartCounter(emu, "VsTestPluginCounter");
                HleHarness.Until(() => Read(emu, counter) > 5, "the first counts");
                SaveStates.Save(emu, statePath, timeoutMs: 60_000);
                emu.Stop();
            }

            using var analyst = new HleHarness(_fixture, "bootstate-plugrun", kernelPlugins: false);
            Emulator again = analyst.Emu;
            SaveStates.BootFromState(again, statePath);
            Emulator kernel = Assert.IsType<Emulator>(again.Vita.KernelProcess);
            LoadedModule restored = Assert.Single(kernel.Kernel.Modules.Values, m => m.Name == "BootStatePlugin");
            Assert.True(restored.IsKernel);
            Assert.Equal(pluginSegment, restored.Segments[0].Address);
            Assert.True(kernel.Linker.TryGetFunctionExport(TargetLib, TargetNid, out uint entry), "the plugin's export is in the kernel's library database");
            Assert.NotEqual(0u, entry);
            SaveStates.Load(again, statePath);
            Assert.Single(again.Kernel.Threads, t => t.Name == "VsTestPluginCounter");
            again.Stop();
        }
        finally
        {
            File.Delete(statePath);
        }
    }

    [Fact]
    public void AStateOfAnEarlierBuildWithoutABootSectionIsRefused()
    {
        string statePath = Path.Combine(Path.GetTempPath(), $"vitasharp-bootstate-{Guid.NewGuid():N}.vss");
        File.WriteAllBytes(statePath, [1, 2, 3]);
        try
        {
            Assert.False(SaveStates.CanBootFromState(statePath));
            using var analyst = new HleHarness(_fixture, "bootstate-none");
            Assert.ThrowsAny<Exception>(() => SaveStates.BootFromState(analyst.Emu, statePath));
            Assert.Null(analyst.Emu.MainModule);
        }
        finally
        {
            File.Delete(statePath);
        }
    }
}
