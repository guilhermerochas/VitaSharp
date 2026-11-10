using VitaSharp.Core;
using VitaSharp.Core.Kernel;
using Xunit;

namespace VitaSharp.Tests;

/// <summary>Launch requests handed to the shell (sceAppMgrLaunchAppByName / ByUri from an application, and the host's
/// --launch-app / --launch-uri): the application's entry waits for the shell's second stage and the shell gets event 6;
/// the shell's system screen (lock screen) state and the shell-up signal the host waits for.</summary>
[Collection("guest")]
public sealed class ShellLaunchRequestTests(GuestMemoryFixture fx) : IDisposable
{
    readonly string _root = Path.Combine(Path.GetTempPath(), "vitasharp-shell-launch-" + Guid.NewGuid().ToString("N"));
    Emulator? _emu;

    Emulator Emu()
    {
        if (_emu != null) return _emu;
        Directory.CreateDirectory(_root);
        return _emu = new Emulator(new EmulatorOptions { FsRoot = _root, Memory = fx.Memory, EnableJit = false, CpuBackend = "interp" });
    }

    public void Dispose()
    {
        _emu?.Dispose();
        try { Directory.Delete(_root, true); } catch (IOException) { }
    }

    static (int Type, int W1, int W2) Event(byte[] e) =>
        (BitConverter.ToInt32(e, 0), BitConverter.ToInt32(e, 4), BitConverter.ToInt32(e, 8));

    [Fact]
    public void ByNameReservesTheApplicationAndPostsEvent6()
    {
        var emu = Emu();
        while (emu.Vita.TakeShellEvent() != null) { }
        var pl = emu.Vita.RequestLaunchByName(emu, "NPXS10015", "dir", 0x20000, "param");
        Assert.Same(pl, emu.Vita.GetPendingLaunch(pl.App.Uid));
        Assert.Equal(("NPXS10015", "dir", 0x20000, "param"), (pl.TitleId, pl.Dir, pl.Flags, pl.Param));
        Assert.Equal((ShellEvents.LaunchRequest, pl.App.Uid, pl.App.Uid), Event(emu.Vita.TakeShellEvent()!));
        Assert.Same(pl, emu.Vita.TakePendingLaunch(pl.App.Uid));
        Assert.Null(emu.Vita.GetPendingLaunch(pl.App.Uid));
    }

    [Fact]
    public void ByUriIsARequestWithoutAnApplication()
    {
        var emu = Emu();
        while (emu.Vita.TakeShellEvent() != null) { }
        var pl = emu.Vita.RequestLaunchByUri(emu, "psgm:play?titleid=NPXS10015", 0xFFFFF);
        Assert.Equal(("", "", 0xFFFFF | 0x100000, "psgm:play?titleid=NPXS10015"), (pl.TitleId, pl.Dir, pl.Flags, pl.Param));
        Assert.Equal((ShellEvents.LaunchRequest, pl.App.Uid, pl.App.Uid), Event(emu.Vita.TakeShellEvent()!));
    }

    [Fact]
    public void SystemImposeState2IsTheShellsSystemScreen()
    {
        var emu = Emu();
        Assert.False(emu.Vita.ShellImposesSystemUi);
        lock (emu.Vita.AppMgrState) BitConverter.TryWriteBytes(emu.Vita.AppMgrState.AsSpan(0x14), 1);
        Assert.True(emu.Vita.ShellImposesSystemUi);
        lock (emu.Vita.AppMgrState) BitConverter.TryWriteBytes(emu.Vita.AppMgrState.AsSpan(0x14), 0);
        Assert.False(emu.Vita.ShellImposesSystemUi);
    }

    [Fact]
    public void ShellUpIsSetOnceTheShellStartsItsFirstService()
    {
        var emu = Emu();
        Assert.False(emu.Vita.WaitShellUp(0));
        emu.Vita.MarkShellUp();
        Assert.True(emu.Vita.ShellUp);
        Assert.True(emu.Vita.WaitShellUp(0));
    }
}
