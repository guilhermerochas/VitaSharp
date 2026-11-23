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
}
