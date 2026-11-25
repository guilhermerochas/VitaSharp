using VitaSharp.Core;
using VitaSharp.Core.Hle.Modules;
using VitaSharp.Core.Kernel;
using VitaSharp.Core.Memory;
using static VitaSharp.Tests.HleHarness;

namespace VitaSharp.Tests;

/// <summary>
/// SceAppMgr's application names (FW 3.74 kernel appmgr): an application's entry is named by the title its launch
/// names (0x8100B748 / 0x8100BF4C copy it), which is not the executable's folder when the shell starts the PSP emulator
/// (vs0:app/NPXS10028) for a bubble. The status calls report that name and the calls by name look it up. There is one
/// application per name: once a new process is created, an older entry of the same name loses its name and is destroyed
/// with reason 4 (0x81002504), before the new process is announced (event 2). Destroying an application tells the shell
/// at once (event 3, 0x810014F4), well before the process is gone (event 4).
/// Seen with a plugin in the shell that destroys a bubble's application, starts the PSP emulator under the bubble's
/// title (LaunchAppByPath4) and then asks for that title (LaunchAppByName). The shell never saw the emulator as the
/// bubble's application and started a second one, and both kept running. Synthetic titles next to a test shell.
/// </summary>
[Collection("guest")]
public sealed class AppNameTests : IDisposable
{
    private const string BubbleTitle = "VSTS00009";
    private readonly HleHarness _harness;
    private readonly Emulator _shell;
    private readonly List<Emulator> _apps = [];

    public AppNameTests(GuestMemoryFixture fixture)
    {
        // the synthetic title's segment is fixed at the start of user memory: blocks other tests left there go first
        for (uint address = SyntheticTitle.Base; address < SyntheticTitle.Base + 0x0010_0000; address += 0x1000)
        {
            if (fixture.Memory.FindBlock(address) is { } leftover)
            {
                fixture.Memory.Free(leftover.Address);
            }
        }

        _harness = new HleHarness(fixture, "appname");
        _shell = _harness.Emu;
        _shell.LoadTitle(SyntheticTitle.Install(_harness.Root, "VSTS00001"));
        Assert.Equal(0, Call(_harness.Module<AppMgrShell>(), "sceAppMgrDeclareShellProcess2", _harness.Str("NPXS19999")));
        DrainEvents();
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

    /// <summary>The shell events taken since the last <see cref="DrainEvents"/>, in order.</summary>
    private readonly List<(int Type, int[] Words, string Name)> _events = [];

    private void DrainEvents()
    {
        while (_shell.Vita.TakeShellEvent() != null)
        {
        }

        _events.Clear();
    }

    /// <summary>The shell events posted since the last <see cref="DrainEvents"/> (the process tasks post theirs at any
    /// time, so every call returns them all): type, the seven words after it and the name at +0x20 (events 3 and 4).</summary>
    private List<(int Type, int[] Words, string Name)> Events()
    {
        List<(int Type, int[] Words, string Name)> events = _events;
        while (_shell.Vita.TakeShellEvent() is { } e)
        {
            int[] words = new int[7];
            for (int i = 0; i < words.Length; i++)
            {
                words[i] = BitConverter.ToInt32(e, 4 + i * 4);
            }

            string name = System.Text.Encoding.ASCII.GetString(e, 0x20, 0x20).TrimEnd('\0');
            events.Add((BitConverter.ToInt32(e, 0), words, name));
        }

        return events;
    }

    /// <summary>The application of folder <paramref name="folder"/> started as the shell starts it from its LiveArea,
    /// with the launch naming <paramref name="name"/> (sceAppMgrLaunchAppByPath4).</summary>
    private Emulator LaunchByPath(string folder, string name)
    {
        SyntheticTitle.Install(_harness.Root, folder);
        int before = _shell.Vita.Processes.Count(p => p.Running);
        Assert.Equal(0, Call(_harness.Module<SystemSoftware>(), "sceAppMgrLaunchAppByPath4", $"ux0:app/{folder}/eboot.bin", _harness.Str(name), 0u, 0u, 0u, 0u));
        Emulator app = _shell.Vita.Processes.Last(p => !p.IsShell && p.TitleId == folder && p.Running);
        _apps.Add(app);
        Assert.True(_shell.Vita.Processes.Count(p => p.Running) <= before + 1);
        return app;
    }

    private static void WaitReleased(Emulator app)
    {
        // the release runs on a host task, maybe before a Released handler could be added: wait for its effect
        Until(() => app.Vita.ByPid(app.Pid) == null, "the process's release");
    }

    [Fact]
    public void TheApplicationIsKnownByTheTitleItsLaunchNamed()
    {
        Emulator app = LaunchByPath("VSTS00002", BubbleTitle);
        Assert.Equal(BubbleTitle, app.AppName);
        Assert.Same(app, _shell.Vita.ByAppName(BubbleTitle));
        Assert.Null(_shell.Vita.ByAppName("VSTS00002")); // the executable's folder is not the application's name

        uint status = _harness.Alloc(0x80);
        Assert.Equal(0, Call(_harness.Module<AppMgrShell>(), "sceAppMgrGetStatusByAppId", app.AppId, status));
        Assert.Equal(BubbleTitle, GuestMemory.ReadCString(status + 0x0C, 0x20));
        Assert.Equal((uint)app.AppId, GuestMemory.R32(status + 0x30));
    }

    [Fact]
    public void ANewProcessOfANameDestroysTheOlderOneBeforeItIsAnnounced()
    {
        Emulator first = LaunchByPath("VSTS00002", BubbleTitle);
        DrainEvents();

        Emulator second = LaunchByPath("VSTS00003", BubbleTitle);

        Assert.False(first.Running);
        Assert.True(second.Running);
        Assert.Equal("", first.AppName);
        Assert.Same(second, _shell.Vita.ByAppName(BubbleTitle));
        List<(int Type, int[] Words, string Name)> events = Events();
        int destroyed = events.FindIndex(e => e.Type == ShellEvents.AppTerminating && e.Words[0] == first.Pid);
        int created = events.FindIndex(e => e.Type == ShellEvents.AppCreated && e.Words[0] == second.Pid);
        Assert.True(destroyed >= 0, "no event 3 for the replaced application");
        Assert.True(created > destroyed, "the new process was announced before the older one was destroyed");
        Assert.Equal(Emulator.ReplacedDestroyReason, events[destroyed].Words[1]);
        Assert.Equal(first.AppId, events[destroyed].Words[4]);
        Assert.Equal("", events[destroyed].Name); // its name was cleared first

        WaitReleased(first);
        (int Type, int[] Words, string Name) ended = Events().Single(e => e.Type == ShellEvents.AppEnded && e.Words[0] == first.Pid);
        Assert.Equal("", ended.Name);
        Assert.True(second.Running);
    }

    [Fact]
    public void TheSameExecutableLaunchedAgainUnderItsOwnNameReplacesItsFirstProcess()
    {
        Emulator first = LaunchByPath("VSTS00002", "VSTS00002");
        Emulator second = LaunchByPath("VSTS00002", "VSTS00002");

        Assert.False(first.Running);
        Assert.Same(second, _shell.Vita.ByAppName("VSTS00002"));
    }

    [Fact]
    public void ApplicationsOfOtherNamesStay()
    {
        Emulator first = LaunchByPath("VSTS00002", BubbleTitle);
        Emulator other = LaunchByPath("VSTS00003", "VSTS00003");

        Assert.True(first.Running);
        Assert.True(other.Running);
        Assert.Same(first, _shell.Vita.ByAppName(BubbleTitle));
        Assert.Same(other, _shell.Vita.ByAppName("VSTS00003"));
    }

    [Fact]
    public void DestroyingAnApplicationByNameTellsTheShellAtOnceWithItsName()
    {
        Emulator app = LaunchByPath("VSTS00002", BubbleTitle);
        DrainEvents();

        Assert.Equal(0, Call(_harness.Module<AppMgrShell>(), "sceAppMgrDestroyAppByName", _harness.Str(BubbleTitle)));
        WaitReleased(app);

        List<(int Type, int[] Words, string Name)> events = Events();
        int destroyedAt = events.FindIndex(e => e.Type == ShellEvents.AppTerminating);
        int endedAt = events.FindIndex(e => e.Type == ShellEvents.AppEnded && e.Words[0] == app.Pid);
        Assert.True(destroyedAt >= 0, "no event 3");
        Assert.True(endedAt > destroyedAt, "event 3 came after the process was gone");
        (int Type, int[] Words, string Name) destroyed = events[destroyedAt];
        Assert.Equal(app.Pid, destroyed.Words[0]);
        Assert.Equal(VitaSystem.DestroyReason, destroyed.Words[1]);
        Assert.Equal(app.AppId, destroyed.Words[4]);
        Assert.Equal(BubbleTitle, destroyed.Name);
        Assert.Equal(BubbleTitle, events[endedAt].Name);
    }

    [Fact]
    public void AnApplicationEndingByItselfGetsNoDestroyEvent()
    {
        Emulator app = LaunchByPath("VSTS00002", BubbleTitle);
        DrainEvents();

        app.EndProcess(0);
        WaitReleased(app);

        List<(int Type, int[] Words, string Name)> events = Events();
        Assert.DoesNotContain(events, e => e.Type == ShellEvents.AppTerminating);
        Assert.Contains(events, e => e.Type == ShellEvents.AppEnded && e.Words[0] == app.Pid);
    }

    /// <summary>The shell's own request by name (it asks for NPXS10079, its background download service, this way) is
    /// one like any other in the FW 3.74 kernel appmgr (0x8100D89C): the call returns 0 with no process, the shell gets
    /// event 6, and the application starts only when the shell's handler answers with the second stage. VitaSharp
    /// started the shell's requests at once, as background processes the shell never asked the kernel for.</summary>
    [Fact]
    public void TheShellsOwnLaunchByNameIsARequestItAnswersWithTheSecondStage()
    {
        SyntheticTitle.Install(_harness.Root, "VSTS00004");
        DrainEvents();
        int processesBefore = _shell.Vita.Processes.Count;

        Assert.Equal(0, Call(_harness.Module<AppMgrShell>(), "sceAppMgrLaunchAppByName", 0x20000, _harness.Str("VSTS00004"), _harness.Str("RemoteDownload")));

        Assert.Equal(processesBefore, _shell.Vita.Processes.Count);
        Assert.True(_shell.Vita.ShellUp);
        (int Type, int[] Words, string Name) request = Events().Single(e => e.Type == ShellEvents.LaunchRequest);
        int appId = request.Words[0];
        PendingLaunch? pending = _shell.Vita.GetPendingLaunch(appId);
        Assert.NotNull(pending);
        Assert.Equal(("VSTS00004", 0x20000, "RemoteDownload"), (pending!.TitleId, pending.Flags, pending.Param));

        Assert.Equal(0, Call(_harness.Module<AppMgrShell>(), "sceAppMgrLaunchAppByName2ndStage", appId, 0u, 0x20000, 0, 0u));

        Emulator app = _shell.Vita.ByAppName("VSTS00004")!;
        _apps.Add(app);
        Assert.Equal(appId, app.AppId);
        Assert.True(app.Running);
    }

    /// <summary>Code in the shell asking for a title a process already runs is a request to the shell (event 6), as the
    /// kernel makes it for any caller: the shell's handler decides (it starts the bubble's application, and the older
    /// process of the name goes).</summary>
    [Fact]
    public void LaunchByNameOfARunningTitleFromTheShellIsARequestToTheShell()
    {
        Emulator app = LaunchByPath("VSTS00002", BubbleTitle);
        SyntheticTitle.Install(_harness.Root, BubbleTitle);
        DrainEvents();

        int result = Call(_harness.Module<AppMgrShell>(), "sceAppMgrLaunchAppByName", 0x60000, _harness.Str(BubbleTitle), 0u);

        Assert.Equal(0, result);
        (int Type, int[] Words, string Name) request = Events().Single(e => e.Type == ShellEvents.LaunchRequest);
        PendingLaunch? pending = _shell.Vita.GetPendingLaunch(request.Words[0]);
        Assert.NotNull(pending);
        Assert.Equal(BubbleTitle, pending!.TitleId);
        Assert.True(app.Running);
    }
}
