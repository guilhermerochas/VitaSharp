using System.Collections;
using System.Reflection;
using VitaSharp.Core.Hle.Modules;
using VitaSharp.Core.SaveState;

namespace VitaSharp.Tests;

/// <summary>
/// sceIoRead reads the host file first and then waits for the device time; a state saved during that wait reissues the
/// call after a load. The file's saved position is where that read started, so the reissued read reads the same bytes
/// again. Before, the position after the read was saved: a Dead Space pre-roll state taken inside the 4.5 MB read of
/// its game library read 0 bytes at the end of the file after the load, and the port stopped with "could not load".
/// </summary>
[Collection("guest")]
public sealed class IoReadReissueStateTests(GuestMemoryFixture fixture)
{
    private const int ORdOnly = 1;

    private static object OpenFileOf(IoFileMgr io, int fd)
    {
        var files = (IDictionary)typeof(IoFileMgr).GetField("_files", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(io)!;
        return files[fd]!;
    }

    private static FileStream StreamOf(object file) => (FileStream)file.GetType().GetField("Stream")!.GetValue(file)!;

    [Fact]
    public void AReadWaitingForItsDeviceTimeIsSavedAtItsStart()
    {
        using var saved = new HleHarness(fixture, "io-reissue-a");
        string dir = Path.Combine(saved.Root, "ux0", "data");
        Directory.CreateDirectory(dir);
        File.WriteAllBytes(Path.Combine(dir, "lib.so"), new byte[100_000]);
        IoFileMgr io = saved.Module<IoFileMgr>();
        int fd = 0;
        saved.OnGuest(_ => fd = io.sceIoOpen("ux0:data/lib.so", ORdOnly, 0));
        Assert.True(fd > 0);

        // the host part of a 60000-byte read from 1000 is done; the call now waits for its device time
        object file = OpenFileOf(io, fd);
        StreamOf(file).Position = 61_000;
        file.GetType().GetField("ReadStart")!.SetValue(file, 1_000L);

        var w = new SnapshotWriter();
        ((ISnapshotState)io).SaveState(w);

        using var booted = new HleHarness(fixture, "io-reissue-b");
        Directory.CreateDirectory(Path.Combine(booted.Root, "ux0", "data"));
        File.WriteAllBytes(Path.Combine(booted.Root, "ux0", "data", "lib.so"), new byte[100_000]);
        IoFileMgr loaded = booted.Module<IoFileMgr>();
        ((ISnapshotState)loaded).LoadState(new SnapshotReader(w.Data.ToArray()) { Objects = booted.Emu.Kernel.Objects });
        Assert.Equal(1_000, StreamOf(OpenFileOf(loaded, fd)).Position);
    }
}
