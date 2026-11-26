using VitaSharp.Core;
using VitaSharp.Core.Util;

namespace VitaSharp.Tests;

/// <summary>
/// <see cref="SessionReport"/>: the end-of-run lines the desktop Runner and the Android session write, and the periodic
/// subset Android puts in its log file while a game runs (the process is often killed instead of stopped).
/// </summary>
[Collection("guest")]
public sealed class SessionReportTests(GuestMemoryFixture fixture)
{
    [Fact]
    public void EndOfRunAndPeriodicLines()
    {
        string root = Path.Combine(Path.GetTempPath(), "vitasharp-report-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        bool jitEnabled = VitaSharp.Core.Cpu.Jit.Enabled;
        string jitMode = VitaSharp.Core.Cpu.Jit.Mode;
        var emu = new Emulator(new EmulatorOptions { FsRoot = root, Memory = fixture.Memory, EnableJit = false, CpuBackend = "interp" });
        try
        {
            var lines = SessionReport.Lines(emu).ToList();
            Assert.StartsWith("Stopping. ", lines[0]);
            Assert.Contains(lines, l => l.StartsWith("  GPU commands: submitted ", StringComparison.Ordinal));
            Assert.Contains(lines, l => l.StartsWith("PERF cpu ", StringComparison.Ordinal));
            Assert.StartsWith("PERF allocated ", lines[^1]);
            Assert.Contains("ms per GC), gen0 before the last young GC ", lines[^1]);
            // the order the desktop always wrote: GPU commands, AUDIO summary, then the host process
            int gpu = lines.FindIndex(l => l.StartsWith("  GPU commands", StringComparison.Ordinal));
            int cpu = lines.FindIndex(l => l.StartsWith("PERF cpu ", StringComparison.Ordinal));
            Assert.True(gpu < cpu);
            Assert.All(lines.Skip(gpu + 1).Take(cpu - gpu - 1), l => Assert.True(l.StartsWith("AUDIO summary", StringComparison.Ordinal) || l.StartsWith("[dbg]", StringComparison.Ordinal), l));

            var periodic = SessionReport.PeriodicLines(emu).ToList();
            Assert.StartsWith("PERF periodic summary: frame ", periodic[0]);
            Assert.Contains(periodic, l => l.StartsWith("  GPU commands: submitted ", StringComparison.Ordinal));
            Assert.Contains(periodic, l => l.StartsWith("PERF cpu ", StringComparison.Ordinal));
            Assert.DoesNotContain(periodic, l => l.StartsWith("Stopping.", StringComparison.Ordinal));
            Assert.DoesNotContain(periodic, l => l.Contains("exception", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            emu.Dispose();
            VitaSharp.Core.Cpu.Jit.Enabled = jitEnabled;
            VitaSharp.Core.Cpu.Jit.Select(jitMode);
            try
            {
                Directory.Delete(root, true);
            }
            catch (IOException) { }
        }
    }

    /// <summary>The GC detail of the PERF allocated line: the pause per collection and the young generation before the
    /// last young GC, which tells a phone log whether the GC runs with a tiny gen0 budget (VitaSharpGcGen0Size).</summary>
    [Fact]
    public void GcDetailReportsPausePerCollectionAndTheYoungGenerationSize()
    {
        byte[]? last = null;
        for (int i = 0; i < 4096; i++)
        {
            last = new byte[512];
        }
        GC.KeepAlive(last);
        GC.Collect(0, GCCollectionMode.Forced, blocking: true);

        string detail = SessionReport.GcDetail();

        Assert.Matches(@"^GC pause total \d+ ms \(\d+[.,]\d\d ms per GC\), gen0 before the last young GC [1-9]\d* KB, memory load \d+% \(GC high-load threshold [1-9]\d*%\)$", detail);
    }
}
