using System.Runtime.CompilerServices;
using VitaSharp.Core;
using VitaSharp.Core.Hle.Modules;
using VitaSharp.Core.Kernel;
using VitaSharp.Core.Memory;
using Xunit;
using static VitaSharp.Tests.HleHarness;
using Semaphore = VitaSharp.Core.Kernel.Semaphore;

namespace VitaSharp.Tests;

/// <summary>
/// The kernel's launches of an application by title id (SceAppMgr 3.74): NID_99AA37C1 (0x8100dc08) posts the launch
/// request and returns; NID_1B6C2FED (0x8100da9c), a development kit's debugger entry, posts it with flags 0x20000 and a
/// callback of its own, and waits on the semaphore "SceAppMgr:semafordbg" until the shell's second stage created the
/// process and notified the callback (whose function signals the semaphore). Both only from the kernel process
/// (0x8080201F); NULL 0x80802016, empty 0x8080201A.
/// </summary>
[Collection("guest")]
public sealed class AppMgrKernelDebugLaunchTests : IDisposable
{
    private const int NotKernel = unchecked((int)0x8080201F), NullName = unchecked((int)0x80802016), EmptyName = unchecked((int)0x8080201A);
    private const int Retail = unchecked((int)0x80020012);
    private const string Title = "VSTS00004";

    private readonly HleHarness _harness;
    private readonly List<Emulator> _apps = [];

    public AppMgrKernelDebugLaunchTests(GuestMemoryFixture fixture)
    {
        // the synthetic title's segment is fixed at the start of user memory: blocks other tests left there
        for (uint address = SyntheticTitle.Base; address < SyntheticTitle.Base + 0x0010_0000; address += 0x1000)
        {
            if (fixture.Memory.FindBlock(address) is { } leftover)
            {
                fixture.Memory.Free(leftover.Address);
            }
        }

        _harness = new HleHarness(fixture, "appmgrdbg");
    }

    public void Dispose()
    {
        foreach (Emulator app in _apps)
        {
            app.Stop();
        }

        Invoke(Services, "ksceKernelClearDipsw", 0x9Fu);
        _harness.Dispose();
    }

    private AppMgrShell Shell => _harness.Module<AppMgrShell>();

    private SystemServicesVsh Services => _harness.Module<SystemServicesVsh>();

    private ObjectTable Objects => _harness.Emu.Kernel.Objects;

    private void DevelopmentKitWithShell()
    {
        Invoke(Services, "ksceKernelSetDipsw", 0x9Fu);
        Assert.Equal(0, Call(Shell, "sceAppMgrDeclareShellProcess2", _harness.Str("NPXS19999")));
        while (_harness.Emu.Vita.TakeShellEvent() != null)
        {
        }
    }

    /// <summary>Runs the kernel entry on a guest thread of the kernel process; the run's result is the entry's.</summary>
    private (GuestRun Run, StrongBox<int> Result) StartFromKernel(string entry, params object[] args)
    {
        // the thread may end before Start returns: the result goes to a box made before it starts
        var result = new StrongBox<int>();
        GuestRun run = _harness.Start(_ =>
        {
            using (ProcessScope.Enter(KernelSpace.KernelPid))
            {
                result.Value = Call(Shell, entry, args);
            }
        }, "kernel-launcher");
        return (run, result);
    }

    private int FromKernel(string entry, params object[] args)
    {
        using (ProcessScope.Enter(KernelSpace.KernelPid))
        {
            return Call(Shell, entry, args);
        }
    }

    [Fact]
    public void TheDebugLaunchIsADevelopmentKitsKernelEntry()
    {
        uint name = _harness.Str(Title);
        Assert.Equal(Retail, FromKernel("ksceAppMgr_1B6C2FED", name, 0u));
        Invoke(Services, "ksceKernelSetDipsw", 0x9Fu);
        // the caller here is the test's process, not the kernel's
        Assert.Equal(NotKernel, Call(Shell, "ksceAppMgr_1B6C2FED", name, 0u));
        Assert.Equal(NotKernel, Call(Shell, "ksceAppMgr_99AA37C1", name));
        Assert.Null(Objects.FindByName<Semaphore>("SceAppMgr:semafordbg"));
    }

    /// <summary>The call returns once the second stage created the process: until then the request waits for the shell
    /// (event 6, flags 0x20000, the parameter) and the caller waits on the module's semaphore; afterwards the semaphore
    /// and the callback are gone.</summary>
    [Fact]
    public void TheDebugLaunchReturnsOnceTheShellCreatedTheProcess()
    {
        SyntheticTitle.Install(_harness.Root, Title);
        DevelopmentKitWithShell();
        (GuestRun run, StrongBox<int> result) = StartFromKernel("ksceAppMgr_1B6C2FED", _harness.Str(Title), _harness.Str("-debug"));
        Until(() => Objects.FindByName<Callback>("SceAppMgr:cbkfordbg") is { } callback
            && PendingOf(callback) != null, "the debug launch's request");
        Callback cbk = Objects.FindByName<Callback>("SceAppMgr:cbkfordbg")!;
        PendingLaunch pending = PendingOf(cbk)!;
        Assert.Equal((Title, 0x20000, "-debug"), (pending.TitleId, pending.Flags, pending.Param));
        byte[] shellEvent = _harness.Emu.Vita.TakeShellEvent()!;
        Assert.Equal((ShellEvents.LaunchRequest, pending.App.Uid), (BitConverter.ToInt32(shellEvent, 0), BitConverter.ToInt32(shellEvent, 4)));
        Semaphore semaphore = Objects.FindByName<Semaphore>("SceAppMgr:semafordbg")!;
        Assert.Equal((0, 1), (semaphore.Count, semaphore.MaxCount));
        Assert.False(run.Thread.WaitForExit(0), "the debug launch returned before the process existed");

        Assert.Equal(0, Call(Shell, "sceAppMgrLaunchAppByName2ndStage", pending.App.Uid, 0u, 0x20000, 0, 0u));
        _apps.Add(_harness.Emu.Vita.Processes.Single(p => p.TitleId == Title));
        _harness.Join(run);
        Assert.Equal(0, result.Value);
        Assert.Null(Objects.FindByName<Semaphore>("SceAppMgr:semafordbg"));
        Assert.Null(Objects.FindByName<Callback>("SceAppMgr:cbkfordbg"));
    }

    /// <summary>As in the module, the NULL/empty name is the launch's result but the wait comes first: the call returns
    /// it only once the semaphore is signalled.</summary>
    [Fact]
    public void AnEmptyNameIsReturnedAfterTheWait()
    {
        DevelopmentKitWithShell();
        foreach ((uint name, int expected) in new[] { (_harness.Str(""), EmptyName), (0u, NullName) })
        {
            (GuestRun run, StrongBox<int> result) = StartFromKernel("ksceAppMgr_1B6C2FED", name, 0u);
            Until(() => Objects.FindByName<Semaphore>("SceAppMgr:semafordbg") is { } semaphore && semaphore.Uid != 0
                && Objects.FindByName<Callback>("SceAppMgr:cbkfordbg") != null, "the debug launch's semaphore");
            Assert.Null(_harness.Emu.Vita.TakeShellEvent());
            Assert.False(run.Thread.WaitForExit(0));
            Semaphore semaphore = Objects.FindByName<Semaphore>("SceAppMgr:semafordbg")!;
            Assert.Equal(0, semaphore.Signal(1));
            _harness.Join(run);
            Assert.Equal(expected, result.Value);
            Assert.Null(Objects.FindByName<Semaphore>("SceAppMgr:semafordbg"));
        }
    }

    /// <summary>NID_99AA37C1 posts the request with no flags, parameter or callback and returns at once.</summary>
    [Fact]
    public void TheKernelLaunchByNamePostsTheRequest()
    {
        SyntheticTitle.Install(_harness.Root, Title);
        DevelopmentKitWithShell();
        Assert.Equal(NullName, FromKernel("ksceAppMgr_99AA37C1", 0u));
        Assert.Equal(EmptyName, FromKernel("ksceAppMgr_99AA37C1", _harness.Str("")));
        Assert.Equal(unchecked((int)0x80800006), FromKernel("ksceAppMgr_99AA37C1", _harness.Str("NONE00000")));
        Assert.Equal(0, FromKernel("ksceAppMgr_99AA37C1", _harness.Str(Title)));
        byte[] shellEvent = _harness.Emu.Vita.TakeShellEvent()!;
        PendingLaunch pending = _harness.Emu.Vita.GetPendingLaunch(BitConverter.ToInt32(shellEvent, 4))!;
        Assert.Equal((Title, 0, null, 0), (pending.TitleId, pending.Flags, pending.Param, pending.Callback));
    }

    private PendingLaunch? PendingOf(Callback callback)
    {
        foreach (AppObject app in Objects.All<AppObject>())
        {
            if (_harness.Emu.Vita.GetPendingLaunch(app.Uid) is { } pending && pending.Callback == callback.Uid)
            {
                return pending;
            }
        }

        return null;
    }
}
