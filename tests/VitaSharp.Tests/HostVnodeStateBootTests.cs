using VitaSharp.Core.Hle.Modules;
using VitaSharp.Core.SaveState;

namespace VitaSharp.Tests;

/// <summary>
/// A state saved after a kernel plugin hooked the vfs (FdFix on ksceVopLookup) holds the host vnodes' op functions,
/// which IoFileMgr builds on first use. A session booted from that state (the F8 capture's re-run) has not built them
/// when the function table loads: the load was refused ("host function SceIofilemgrHostVnode:open of the saved
/// session does not exist here") and the phone's Dead Space capture could not be re-run on the PC. The module builds
/// the table again after the load, so the saved functions are never called and the load goes on.
/// </summary>
[Collection("guest")]
public sealed class HostVnodeStateBootTests(GuestMemoryFixture fixture)
{
    [Fact]
    public void AStateWithHostVnodeOpsLoadsIntoASessionThatHasNotBuiltThem()
    {
        using var saved = new HleHarness(fixture, "vnode-state-a");
        Directory.CreateDirectory(Path.Combine(saved.Root, "ux0", "data"));
        uint vnode = 0;
        saved.OnGuest(_ => vnode = saved.Module<IoFileMgr>().HostVnode("ux0:data"));
        Assert.NotEqual(0u, vnode);

        var w = new SnapshotWriter();
        saved.Emu.Hle.SaveState(w);
        byte[] functions = w.Data.ToArray();

        using var booted = new HleHarness(fixture, "vnode-state-b");
        Exception? error = Record.Exception(() => booted.Emu.Hle.LoadState(new SnapshotReader(functions)));
        Assert.Null(error);
    }
}
