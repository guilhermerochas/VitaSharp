using VitaSharp.Core;
using VitaSharp.Core.Hle.Modules;
using VitaSharp.Core.Kernel;
using VitaSharp.Core.Memory;
using static VitaSharp.Tests.HleHarness;

namespace VitaSharp.Tests;

/// <summary>
/// The end of a process and its threads. On the Vita the kill ends every thread of the process, and none runs after
/// it. VitaSharp started a thread of a process that was already being ended (its start task came after the end, or a
/// module_start / main thread was started then): the stop had passed, the thread ran on, the end's wait gave up after
/// 10 s and gave the memory back, and the thread crashed on it ("Prefetch abort: jump to unmapped address", seen in
/// AppNameTests when an application is ended or destroyed right after its launch). Now a process being ended starts
/// no thread, a stop is never undone by a start running at the same time, and the memory goes back only once the
/// threads have left. Synthetic titles next to a test shell.
/// </summary>
[Collection("guest")]
public sealed class ProcessEndThreadTests : IDisposable
{
    private readonly HleHarness _harness;
    private readonly Emulator _shell;
    private readonly List<Emulator> _apps = [];

    public ProcessEndThreadTests(GuestMemoryFixture fixture)
    {
        for (uint address = SyntheticTitle.Base; address < SyntheticTitle.Base + 0x0010_0000; address += 0x1000)
        {
            if (fixture.Memory.FindBlock(address) is { } leftover)
            {
                fixture.Memory.Free(leftover.Address);
            }
        }

        _harness = new HleHarness(fixture, "procend");
        _shell = _harness.Emu;
        _shell.LoadTitle(SyntheticTitle.Install(_harness.Root, "VSTS00001"));
        Assert.Equal(0, Call(_harness.Module<AppMgrShell>(), "sceAppMgrDeclareShellProcess2", _harness.Str("NPXS19999")));
    }

    public void Dispose()
    {
        foreach (Emulator app in _apps)
        {
            app.Stop();
        }

        _shell.Stop();
        _harness.Dispose();
    }

    private Emulator Launch(string folder)
    {
        SyntheticTitle.Install(_harness.Root, folder);
        Assert.Equal(0, Call(_harness.Module<SystemSoftware>(), "sceAppMgrLaunchAppByPath4", $"ux0:app/{folder}/eboot.bin", _harness.Str(folder), 0u, 0u, 0u, 0u));
        Emulator app = _shell.Vita.Processes.Last(p => !p.IsShell && p.TitleId == folder && p.Running);
        _apps.Add(app);
        return app;
    }

    /// <summary>The synthetic title's frame loop, as a thread of <paramref name="app"/>.</summary>
    private static GuestThread LoopThread(Emulator app)
    {
        using (AddressSpace.Use(app.Space))
        {
            return app.Kernel.CreateThread("vs-test-loop", SyntheticTitle.Base + 0x200, 0x10000100, 0x4000, 0, 0x70000);
        }
    }

    [Fact]
    public void AProcessBeingEndedStartsNoThread()
    {
        Emulator app = Launch("VSTS00002");
        GuestThread thread = LoopThread(app);

        app.Terminate("ended by the test");

        Assert.Equal(SceError.ERROR, thread.Start(0, 0));
        Assert.Equal(ThreadStatus.Dormant, thread.Status);
        Assert.True(thread.WaitForExit(0));
    }

    /// <summary>A thread running the title's loop (it waits for the vblank in the host) when the process is ended
    /// stops, and the memory goes back only after: when <see cref="Emulator.Released"/> comes, every thread of the
    /// process has left and none crashed.</summary>
    [Fact]
    public void ARunningThreadStopsBeforeTheMemoryGoesBack()
    {
        Emulator app = Launch("VSTS00002");
        GuestThread thread = LoopThread(app);
        Assert.Equal(0, thread.Start(0, 0));
        Until(() => thread.Status == ThreadStatus.Running || thread.BlockedInHost, "the loop thread running");
        var fatal = new List<Exception>();
        app.Fatal += (_, e) => fatal.Add(e);
        bool allExitedAtRelease = false;
        app.ReleaseStarting += () => allExitedAtRelease = app.Kernel.Threads.All(t => t.WaitForExit(0));

        app.Terminate("ended by the test");

        Until(() => app.Vita.ByPid(app.Pid) == null, "the process's release");
        Assert.True(allExitedAtRelease, "the memory went back while a thread of the process still ran");
        Assert.Empty(fatal);
    }

    /// <summary>Applications ended or destroyed right after their launch (their start task may come after the end):
    /// when the release starts none of their threads runs, and nothing crashes.</summary>
    [Fact]
    public void ApplicationsEndedRightAfterTheirLaunchLeaveNoThreadBehind()
    {
        for (int i = 0; i < 8; i++)
        {
            Emulator app = Launch(i % 2 == 0 ? "VSTS00002" : "VSTS00003");
            var fatal = new List<Exception>();
            app.Fatal += (_, e) => fatal.Add(e);
            bool? allExitedAtRelease = null;
            app.ReleaseStarting += () => allExitedAtRelease = app.Kernel.Threads.All(t => t.WaitForExit(0));
            if (i % 2 == 0)
            {
                app.EndProcess(0);
            }
            else
            {
                Assert.Equal(0, Call(_harness.Module<AppMgrShell>(), "sceAppMgrDestroyAppByName", _harness.Str("VSTS00003")));
            }

            Until(() => app.Vita.ByPid(app.Pid) == null, "the process's release");
            Assert.True(allExitedAtRelease, $"run {i}: the memory went back while a thread of the ended process still ran");
            Assert.Empty(fatal);
        }
    }
}
