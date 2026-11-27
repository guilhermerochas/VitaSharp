using VitaSharp.Core.Hle.Modules;
using VitaSharp.Core.Io;
using VitaSharp.Core.Kernel;

namespace VitaSharp.Tests;

/// <summary>
/// The F8 capture lists every IoFileMgr path call that failed since the boot (io_failures.txt): a file a game cannot
/// open on one host only (the Dead Space silence on the phone, data copied with another spelling) is then named by
/// any capture, without an I/O trace from the boot. Successful calls are not listed; repeats are counted on one line.
/// </summary>
[Collection("guest")]
public sealed class IoFailureLogTests : IDisposable
{
    private const int ORdOnly = 1;

    private readonly HleHarness _h;

    public IoFailureLogTests(GuestMemoryFixture fixture)
    {
        _h = new HleHarness(fixture, "io-failures");
        string dir = Path.Combine(_h.Root, "ux0", "data", "game");
        Directory.CreateDirectory(dir);
        File.WriteAllBytes(Path.Combine(dir, "present.bin"), [1, 2, 3]);
    }

    public void Dispose()
    {
        _h.Dispose();
    }

    private IoFileMgr Io => _h.Module<IoFileMgr>();

    [Fact]
    public void FailedPathCallsAreListedWithPathErrorAndCount()
    {
        int okFd = 0;
        int missing1 = 0;
        int missing2 = 0;
        int noDir = 0;
        int noFile = 0;
        _h.OnGuest(_ =>
        {
            okFd = Io.sceIoOpen("ux0:data/game/present.bin", ORdOnly, 0);
            missing1 = Io.sceIoOpen("ux0:data/game/sounds/bank.snd", ORdOnly, 0);
            missing2 = Io.sceIoOpen("ux0:data/game/sounds/bank.snd", ORdOnly, 0);
            noDir = Io.sceIoDopen("ux0:data/game/music");
            noFile = Io.sceIoRemove("ux0:data/game/gone.bin");
        });

        Assert.True(okFd > 0);
        Assert.Equal(SceError.IO_ENOENT, missing1);
        Assert.Equal(SceError.IO_ENOENT, missing2);
        Assert.True(noDir < 0);
        Assert.True(noFile < 0);

        IoFailureLog log = Io.Failures;
        Assert.Equal(4, log.Total);
        string[] lines = log.Lines().ToArray();
        string[] rows = lines.Where(l => !l.StartsWith('#')).ToArray();
        Assert.Equal(3, rows.Length);
        Assert.Equal("sceIoOpen\t0x80010002\t2", string.Join('\t', rows[0].Split('\t').Take(3)));
        Assert.EndsWith("\tux0:data/game/sounds/bank.snd", rows[0]);
        Assert.StartsWith("sceIoDopen\t", rows[1]);
        Assert.EndsWith("\tux0:data/game/music", rows[1]);
        Assert.StartsWith("sceIoRemove\t", rows[2]);
        Assert.DoesNotContain(lines, l => l.Contains("present.bin", StringComparison.Ordinal));
        Assert.DoesNotContain(lines, l => l.Contains(_h.Root, StringComparison.OrdinalIgnoreCase)); // guest paths only
    }

    [Fact]
    public void DistinctEntriesAreBoundedAndTheRestCounted()
    {
        var log = new IoFailureLog();
        for (int i = 0; i < IoFailureLog.MaxEntries + 10; i++)
        {
            Assert.Equal(-5, log.Note("sceIoOpen", $"ux0:data/f{i}", -5));
        }

        Assert.Equal(7, log.Note("sceIoOpen", "ux0:data/ok", 7));
        Assert.Equal(IoFailureLog.MaxEntries + 10, log.Total);
        string[] lines = log.Lines().ToArray();
        Assert.Equal(IoFailureLog.MaxEntries, lines.Count(l => !l.StartsWith('#')));
        Assert.Contains("10 beyond the first", lines[0], StringComparison.Ordinal);
    }
}
