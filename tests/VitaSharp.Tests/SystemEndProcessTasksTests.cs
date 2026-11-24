using VitaSharp.Core;
using VitaSharp.Core.Kernel;
using VitaSharp.Core.Memory;
using Xunit;

namespace VitaSharp.Tests;

/// <summary>
/// The system's end waits for the processes still ending: a terminated process joins its threads on a host task and
/// only then frees its memory by its pid, and pids are unique only within one system. An end left running after the
/// system freed, in a guest memory a later system uses, the blocks of that system with the same owner: the return
/// trap of a thread ("kernel:trap"), reallocated to the exception manager's default handler, made a call return into
/// an undefined instruction (CI, HleCostTests), and the hook tests read records of the plugin framework as zero.
/// The end also leaves no guest thread of the system running: a child's start left on its host task made the child
/// running again after the stop, and its main thread went on storing into the next tests' kernel blocks (the hook
/// tests' framework records read as zero). Synthetic: the shell's process of a system in the shared guest memory opens
/// one application whose thread is held in host code, so the application's end is still running when its system
/// ends.
/// </summary>
[Collection("guest")]
public sealed class SystemEndProcessTasksTests(GuestMemoryFixture fixture)
{
    /// <summary>A system (its shell's process) with one application whose thread waits for <paramref name="gate"/>.</summary>
    private sealed class ShellAndApplication : IDisposable
    {
        public HleHarness Harness { get; }
        public Emulator App { get; }

        public ShellAndApplication(GuestMemoryFixture fixture, string tag, ManualResetEventSlim gate)
        {
            // the title's segment is fixed at the start of user memory: blocks other tests of the shared memory left there
            for (uint address = SyntheticTitle.Base; address < SyntheticTitle.Base + 0x0010_0000; address += 0x1000)
            {
                if (fixture.Memory.FindBlock(address) is { } leftover)
                {
                    fixture.Memory.Free(leftover.Address);
                }
            }

            Harness = new HleHarness(fixture, tag);
            Harness.Emu.LoadTitle(SyntheticTitle.Install(Harness.Root, "VSTS00001"));
            App = Harness.Emu.SpawnProcess(SyntheticTitle.Install(Harness.Root, "VSTS00002"), "eboot.bin", []);
            GuestThread held = App.Kernel.CreateThread("held", 0, 0x10000100, 0x10000, 0, 0x70000);
            held.HostBody = _ => gate.Wait();
            Assert.Equal(0, held.Start(0, 0));
        }

        public void Dispose()
        {
            App.Stop();
            Harness.Dispose();
        }
    }

    /// <summary>The application of a system ends, then the system (on another thread): the application's thread is
    /// still held, so its end is still running.</summary>
    private sealed class EndingSystem
    {
        private readonly ManualResetEventSlim _released = new();
        private readonly Task _end;
        private bool _endReturned;

        public bool ReleasedBeforeTheEndReturned { get; private set; }

        public EndingSystem(ShellAndApplication system)
        {
            VitaSystem vita = system.Harness.Emu.Vita;
            system.App.Released += () =>
            {
                ReleasedBeforeTheEndReturned = !Volatile.Read(ref _endReturned);
                _released.Set();
            };

            system.App.Terminate("closed by the test");
            _end = Task.Run(() =>
            {
                system.Dispose();
                Volatile.Write(ref _endReturned, true);
            });
            // the end either waits for the application's (it is held until the gate opens) or returns at once
            HleHarness.Until(() => vita.WaitingForProcessTasks || Volatile.Read(ref _endReturned), "the system's end");
        }

        public bool EndReturned => Volatile.Read(ref _endReturned);

        /// <summary>Waits for the system's end.</summary>
        public void WaitForTheEnd()
        {
            Assert.True(_end.Wait(120_000), "the system did not end");
        }

        /// <summary>Waits for the application's release (after its gate opened).</summary>
        public void WaitForTheRelease()
        {
            Assert.True(_released.Wait(120_000), "the application was not released");
            _released.Dispose();
        }
    }

    [Fact]
    public void AnApplicationEndingWhenTheSystemEndsIsReleasedBeforeTheEndReturns()
    {
        using var gate = new ManualResetEventSlim();
        var ending = new EndingSystem(new ShellAndApplication(fixture, "sysend", gate));
        gate.Set();
        ending.WaitForTheEnd();
        ending.WaitForTheRelease();
        Assert.True(ending.ReleasedBeforeTheEndReturned, "the application's memory went after its system ended");
    }

    [Fact]
    public void AnEndedSystemsProcessDoesNotFreeTheBlocksOfALaterSystemWithTheSamePid()
    {
        using var firstGate = new ManualResetEventSlim();
        var first = new ShellAndApplication(fixture, "sysend1", firstGate);
        int pid = first.App.Pid;
        var ending = new EndingSystem(first);
        bool waited = !ending.EndReturned;
        if (waited)
        {
            firstGate.Set(); // the end waits for the application: it goes before the end returns
            ending.WaitForTheRelease();
        }

        ending.WaitForTheEnd();

        // a later system in the same guest memory: its processes get the same pids, and their blocks those owners
        using var secondGate = new ManualResetEventSlim(true);
        using var second = new ShellAndApplication(fixture, "sysend2", secondGate);
        Assert.Equal(pid, second.App.Pid);
        uint trap = second.App.Kernel.ThreadExitTrap;
        GuestMemory.Block? block = fixture.Memory.FindBlock(trap);
        Assert.NotNull(block);
        Assert.Equal(pid, block.Owner);

        // an end that did not wait: the first system's application ends now, with the second system running; what
        // it frees by its pid must not be the second system's
        if (!waited)
        {
            firstGate.Set();
            ending.WaitForTheRelease();
        }

        Assert.Same(block, fixture.Memory.FindBlock(trap));
        Assert.Equal(0xEF000000u | Kernel.TrapSvc, GuestMemory.R32(trap));
    }

    [Fact]
    public void AChildsStartAfterItsSystemsEndBeganMakesNoThread()
    {
        using var gate = new ManualResetEventSlim(true);
        var system = new ShellAndApplication(fixture, "sysend3", gate);
        Emulator app = system.App;
        system.Dispose();
        int threads = app.Kernel.Threads.Count();

        // what a start left on its host task does when it comes after the end began
        app.Start();
        try
        {
            Assert.Equal(threads, app.Kernel.Threads.Count());
            Assert.DoesNotContain(app.Kernel.Threads, thread => thread.IsLive);
        }
        finally
        {
            app.Stop();
            app.Kernel.WaitAllThreadsExit(10_000);
        }
    }

    [Fact]
    public void NoGuestThreadOfTheSystemRunsAfterItsEnd()
    {
        using var gate = new ManualResetEventSlim(true);
        var system = new ShellAndApplication(fixture, "sysend4", gate);
        Emulator shell = system.Harness.Emu;
        Emulator app = system.App;
        HleHarness.Until(() => app.Kernel.MainThread is { IsLive: true }, "the application's main thread");

        // the application is not stopped first: the system's end stops it and waits for its threads
        system.Harness.Dispose();
        Assert.DoesNotContain(app.Kernel.Threads, thread => thread.IsLive);
        Assert.DoesNotContain(shell.Kernel.Threads, thread => thread.IsLive);
    }
}
