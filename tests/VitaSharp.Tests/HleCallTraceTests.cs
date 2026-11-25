using VitaSharp.Core.Capture;

namespace VitaSharp.Tests;

/// <summary>
/// The comparison of a capture's HLE calls with its re-run's (hle_calls.tsv): thread by thread, the first call to
/// another function or with another result is named, with its guest time; a call still running at the stop ("-") is
/// no difference, and threads only one side has are not compared.
/// </summary>
public sealed class HleCallTraceTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "vitasharp-hletrace-" + Guid.NewGuid().ToString("N"));

    public HleCallTraceTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, true);

    private string Write(string name, params string[] calls)
    {
        string path = Path.Combine(_dir, name);
        File.WriteAllLines(path, calls.Prepend("# guestUs\tthread\tfunction\tlibrary\tr0\tr1\tr2\tr3\tresult\tlr"));
        return path;
    }

    private static string Call(long us, string thread, string function, string result) =>
        $"{us}\t{thread}\t{function}\tSceLib\t0x0\t0x0\t0x0\t0x0\t{result}\t0x81000000";

    [Fact]
    public void TheSameCallsAndResultsHaveNoDifference()
    {
        string a = Write("a.tsv", Call(10, "main", "sceCtrlPeekBufferPositive", "0x00000001"), Call(20, "main", "sceDisplayWaitVblankStart", "-"),
            Call(15, "audio", "sceAudioOutOutput", "0x00000000"));
        string b = Write("b.tsv", Call(11, "main", "sceCtrlPeekBufferPositive", "0x00000001"), Call(25, "main", "sceDisplayWaitVblankStart", "0x00000000"),
            Call(16, "audio", "sceAudioOutOutput", "0x00000000"), Call(30, "only-in-rerun", "sceKernelDelayThread", "0x00000000"));
        Assert.Null(HleCallTrace.FirstDifference(a, b));
    }

    [Fact]
    public void TheEarliestDifferenceOfAnyThreadIsNamed()
    {
        string a = Write("a.tsv", Call(10, "main", "sceIoOpen", "0x00000005"), Call(40, "main", "sceIoRead", "0x00000100"),
            Call(20, "loader", "sceKernelWaitSema", "0x00000000"), Call(30, "loader", "sceKernelSignalSema", "0x00000000"));
        string b = Write("b.tsv", Call(10, "main", "sceIoOpen", "0x80010002"), Call(40, "main", "sceIoRead", "0x80020323"),
            Call(20, "loader", "sceKernelWaitSema", "0x00000000"), Call(30, "loader", "sceKernelDeleteSema", "0x00000000"));
        string? difference = HleCallTrace.FirstDifference(a, b);
        Assert.NotNull(difference);
        Assert.Contains("thread 'main' call 1: sceIoOpen returned 0x00000005 in the capture, 0x80010002 in the re-run", difference);
        Assert.Contains("0.000 s", difference);
    }

    [Fact]
    public void AnotherFunctionIsADifference()
    {
        string a = Write("a.tsv", Call(1_000_000, "worker", "sceKernelWaitEventFlag", "0x00000000"), Call(1_500_000, "worker", "sceKernelDelayThread", "0x00000000"));
        string b = Write("b.tsv", Call(1_000_000, "worker", "sceKernelWaitEventFlag", "0x00000000"), Call(1_500_000, "worker", "sceKernelPollEventFlag", "0x00000000"));
        Assert.Equal("thread 'worker' call 2: sceKernelDelayThread in the capture, sceKernelPollEventFlag in the re-run (guest time 1.500 s)",
            HleCallTrace.FirstDifference(a, b));
    }

    /// <summary>The re-run records from the state's moment, the capture from a little after it: the re-run's first
    /// calls that the capture does not have are skipped, the rest is compared.</summary>
    [Fact]
    public void TheReRunsCallsBeforeTheCapturesFirstOneAreSkipped()
    {
        string[] loop = ["sceCtrlPeekBufferPositive", "sceDisplaySetFrameBuf", "sceDisplayWaitVblankStart"];
        string a = Write("a.tsv", Enumerable.Range(1, 12).Select(i => Call(i * 100, "main", loop[i % 3], "0x00000000")).ToArray());
        string b = Write("b.tsv", Enumerable.Range(0, 13).Select(i => Call(i * 100, "main", loop[i % 3], "0x00000000")).ToArray());
        Assert.Null(HleCallTrace.FirstDifference(a, b));
        string c = Write("c.tsv", Enumerable.Range(0, 13).Select(i => Call(i * 100, "main", i == 9 ? "sceKernelExitProcess" : loop[i % 3], "0x00000000")).ToArray());
        Assert.Equal("thread 'main' call 9: sceCtrlPeekBufferPositive in the capture, sceKernelExitProcess in the re-run (guest time 0.001 s)",
            HleCallTrace.FirstDifference(a, c));
    }

    /// <summary>
    /// The capture's trace starts before its state is saved (the game runs on to a safe point), so its first frames
    /// are not in a re-run from that state; and a game makes the same calls every frame, so the first frame of the
    /// re-run whose functions match is not the same frame. Every frame here opens a file (the fd goes up by one per
    /// frame): the capture's calls from the re-run's first guest time on are paired with the re-run's at the same
    /// guest time, and nothing differs. Paired by the first match, frame 3 of the capture met frame 1 of the re-run and
    /// "sceIoOpen returned 0x0000012A ..., 0x0000012C" looked like a divergence.
    /// </summary>
    [Fact]
    public void TheCapturesFramesFromBeforeTheStateAreNotPairedWithTheReRunsFirstOnes()
    {
        static string[] Frame(int frame, long us) =>
        [
            Call(us, "main", "sceCtrlPeekBufferPositive", "0x00000001"),
            Call(us + 100, "main", "sceIoOpen", $"0x{0x128 + frame:X8}"),
            Call(us + 200, "main", "sceIoRead", "0x00000004"),
            Call(us + 300, "main", "sceIoClose", "0x00000000"),
            Call(us + 400, "main", "sceDisplaySetFrameBuf", "0x00000000"),
            Call(us + 500, "main", "sceDisplayWaitVblankStart", "0x00000000"),
        ];

        // the capture: frames 0..9 (the state is saved during frame 2); the re-run: frames 2..9 from the state, its
        // clock a few microseconds off the recording's
        string a = Write("a.tsv", Enumerable.Range(0, 10).SelectMany(f => Frame(f, 1_000_000 + f * 16_667L)).ToArray());
        string b = Write("b.tsv", Enumerable.Range(2, 8).SelectMany(f => Frame(f, 1_000_000 + f * 16_667L + 37)).ToArray());
        Assert.Null(HleCallTrace.FirstDifference(a, b));

        // a real divergence after the state is still found, at its call of the capture
        string c = Write("c.tsv", Enumerable.Range(2, 8).SelectMany(f => Frame(f == 6 ? 99 : f, 1_000_000 + f * 16_667L + 37)).ToArray());
        Assert.Equal("thread 'main' call 38: sceIoOpen returned 0x0000012E in the capture, 0x0000018B in the re-run (guest time 1.100 s)",
            HleCallTrace.FirstDifference(a, c));

        // the re-run's host ran at another pace (a frame every 21 ms of guest time instead of 16.7): by guest time the
        // frames drift apart, cut at the state's guest time they are paired in order
        long stateUs = 1_000_000 + 2 * 16_667L - 1;
        string d = Write("d.tsv", Enumerable.Range(2, 8).SelectMany(f => Frame(f, stateUs + 1 + (f - 2) * 21_000L)).ToArray());
        Assert.Null(HleCallTrace.FirstDifference(a, d, stateUs));
        string e = Write("e.tsv", Enumerable.Range(2, 8).SelectMany(f => Frame(f == 6 ? 99 : f, stateUs + 1 + (f - 2) * 21_000L)).ToArray());
        Assert.StartsWith("thread 'main' call 38: sceIoOpen returned 0x0000012E in the capture, 0x0000018B in the re-run", HleCallTrace.FirstDifference(a, e, stateUs));

        // the guest clock stops when the save starts and the game runs on to a safe point: frame 2's first call has the
        // state's very guest time but was made before the state was taken (the re-run starts after it)
        long frozenUs = 1_000_000 + 2 * 16_667L;
        string g = Write("g.tsv", Frame(2, frozenUs).Skip(1).Concat(Enumerable.Range(3, 7).SelectMany(f => Frame(f, frozenUs + (f - 2) * 21_000L))).ToArray());
        Assert.Null(HleCallTrace.FirstDifference(a, g, frozenUs));

        // the re-run's own trace starts once the game runs again after the load: its first calls after the state
        // (frame 2's peek) are missing; paired by the first frame that matches instead, every fd was one off
        string h = Write("h.tsv", Frame(2, stateUs + 60_000).Skip(1).Concat(Enumerable.Range(3, 7).SelectMany(f => Frame(f, stateUs + 60_000 + (f - 2) * 21_000L))).ToArray());
        Assert.Null(HleCallTrace.FirstDifference(a, h, stateUs));
    }

    /// <summary>
    /// The guest clock is stopped from the start of a save until the game is released, so calls at the state's very
    /// guest time come from before and after it: the capture's trace marks where every thread was when the state was
    /// taken ("#state" lines), and the comparison starts there.
    /// </summary>
    [Fact]
    public void TheCapturesStateMarkSaysWhichCallsCameAfterTheState()
    {
        const long StateUs = 2_000_000;
        // frame 0 before the state; the open of frame 1 before it too (fd 0x101), its read after it, all at the
        // frozen guest time; then frame 2 (fd 0x102)
        string[] capture =
        [
            Call(1_990_000, "main", "sceIoOpen", "0x00000100"), Call(1_990_100, "main", "sceIoRead", "0x00000004"),
            Call(StateUs, "main", "sceIoOpen", "0x00000101"), Call(StateUs, "main", "sceIoRead", "0x00000004"),
            Call(2_020_000, "main", "sceIoOpen", "0x00000102"), Call(2_020_100, "main", "sceIoRead", "0x00000004"),
        ];
        string a = Write("a.tsv", capture.Prepend($"{HleCallTrace.StateMarkLine}\t{StateUs}\tmain\t3").ToArray());
        string b = Write("b.tsv", Call(StateUs + 5, "main", "sceIoRead", "0x00000004"), Call(2_031_000, "main", "sceIoOpen", "0x00000102"),
            Call(2_031_100, "main", "sceIoRead", "0x00000004"));
        Assert.Null(HleCallTrace.FirstDifference(a, b, StateUs));
        // without the mark, the read at the frozen time could be any of the two
        string c = Write("c.tsv", Call(StateUs + 5, "main", "sceIoRead", "0x00000004"), Call(2_031_000, "main", "sceIoOpen", "0x00000103"));
        Assert.StartsWith("thread 'main' call 5: sceIoOpen returned 0x00000102 in the capture, 0x00000103 in the re-run", HleCallTrace.FirstDifference(a, c, StateUs));

        // the thread was blocked in the vblank wait when the state was taken (entered before the mark): the re-run
        // releases it into the same call again, so its trace starts with that call
        string[] blocked =
        [
            Call(1_990_000, "main", "sceIoOpen", "0x00000100"), Call(1_995_000, "main", "sceDisplayWaitVblankStart", "0x00000000"),
            Call(2_010_000, "main", "sceIoOpen", "0x00000101"), Call(2_010_100, "main", "sceDisplayWaitVblankStart", "0x00000000"),
            Call(2_020_000, "main", "sceIoOpen", "0x00000102"),
        ];
        string d = Write("d.tsv", blocked.Prepend($"{HleCallTrace.StateMarkLine}\t{StateUs}\tmain\t2").ToArray());
        string e = Write("e.tsv", Call(StateUs + 9, "main", "sceDisplayWaitVblankStart", "0x00000000"), Call(2_040_000, "main", "sceIoOpen", "0x00000101"),
            Call(2_040_100, "main", "sceDisplayWaitVblankStart", "0x00000000"), Call(2_050_000, "main", "sceIoOpen", "0x00000102"));
        Assert.Null(HleCallTrace.FirstDifference(d, e, StateUs));
    }

    /// <summary>
    /// A thread blocked in a call since before the recording began (the vblank wait it was in when the capture
    /// started) and still in it when the state was taken: the capture's trace has no call of it before the mark, the
    /// re-run releases it into that call again. Its mark says so ("in-call"), and the re-run's first call is not paired
    /// with a capture call: without it the alignment took the capture's next frame and every fd differed by one (macOS
    /// CI, "sceIoOpen returned 0x0000012B in the capture, 0x0000012A in the re-run").
    /// </summary>
    [Fact]
    public void AThreadInACallSinceBeforeTheRecordingIsMarkedAndItsReissuedCallIsNotPaired()
    {
        const long StateUs = 2_410_000;
        string[] capture =
        [
            Call(2_412_000, "main", "sceCtrlPeekBufferPositive", "0x00000001"), Call(2_412_100, "main", "sceIoOpen", "0x0000012A"),
            Call(2_413_000, "main", "sceIoRead", "0x00000004"), Call(2_414_000, "main", "sceIoClose", "0x00000000"),
            Call(2_420_000, "main", "sceDisplaySetFrameBuf", "0x00000000"), Call(2_420_100, "main", "sceDisplayWaitVblankStart", "0x00000000"),
            Call(2_452_000, "main", "sceCtrlPeekBufferPositive", "0x00000001"), Call(2_453_000, "main", "sceIoOpen", "0x0000012B"),
            Call(2_454_000, "main", "sceIoRead", "0x00000004"), Call(2_455_000, "main", "sceIoClose", "0x00000000"),
        ];
        string a = Write("a.tsv", capture.Prepend($"{HleCallTrace.StateMarkLine}\t{StateUs}\tmain\t0\t{HleCallTrace.InCallFlag}").ToArray());
        string[] rerun =
        [
            Call(StateUs + 9, "main", "sceDisplayWaitVblankStart", "0x00000000"),
            Call(2_430_000, "main", "sceCtrlPeekBufferPositive", "0x00000001"), Call(2_430_100, "main", "sceIoOpen", "0x0000012A"),
            Call(2_431_000, "main", "sceIoRead", "0x00000004"), Call(2_432_000, "main", "sceIoClose", "0x00000000"),
            Call(2_440_000, "main", "sceDisplaySetFrameBuf", "0x00000000"), Call(2_440_100, "main", "sceDisplayWaitVblankStart", "0x00000000"),
            Call(2_470_000, "main", "sceCtrlPeekBufferPositive", "0x00000001"), Call(2_471_000, "main", "sceIoOpen", "0x0000012B"),
        ];
        string b = Write("b.tsv", rerun);
        Assert.Null(HleCallTrace.FirstDifference(a, b, StateUs));

        // still a difference where the re-run really does something else
        rerun[8] = Call(2_471_000, "main", "sceIoOpen", "0x0000012C");
        string c = Write("c.tsv", rerun);
        Assert.StartsWith("thread 'main' call 8: sceIoOpen returned 0x0000012B in the capture, 0x0000012C in the re-run", HleCallTrace.FirstDifference(a, c, StateUs));
    }

    /// <summary>The trace marks every thread of a state, also one with no call yet, with whether it was in a call.</summary>
    [Fact]
    public void TheStateMarkNamesAThreadWithoutCallsAndWhetherItWasInACall()
    {
        object waiting = new(), idle = new();
        HleCallTrace.Begin();
        try
        {
            HleCallTrace.Mark([(waiting, "main", true), (idle, "worker", false)]);
            string path = Path.Combine(_dir, HleCallTrace.FileName);
            HleCallTrace.Write(path);
            string[] lines = File.ReadAllLines(path);
            Assert.Contains(lines, l => l.StartsWith(HleCallTrace.StateMarkLine + "\t", StringComparison.Ordinal) && l.EndsWith("\tmain\t0\t" + HleCallTrace.InCallFlag, StringComparison.Ordinal));
            Assert.Contains(lines, l => l.StartsWith(HleCallTrace.StateMarkLine + "\t", StringComparison.Ordinal) && l.EndsWith("\tworker\t0", StringComparison.Ordinal));
        }
        finally
        {
            HleCallTrace.End();
        }
    }
}
