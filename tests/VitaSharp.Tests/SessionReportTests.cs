using VitaSharp.Core;
using VitaSharp.Core.Util;

namespace VitaSharp.Tests;

/// <summary>
/// <see cref="SessionReport"/>: the end-of-run lines the desktop Runner and the Android session write, and the periodic
/// subset Android puts in its log file while a game runs (the process is often killed instead of stopped).
/// </summary>
[Collection("guest")]
public sealed class SessionReportTests(GuestMemoryFixture fx)
{
    [Fact]
    public void EndOfRunAndPeriodicLines()
    {
        string root = Path.Combine(Path.GetTempPath(), "vitasharp-report-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        bool jitEnabled = VitaSharp.Core.Cpu.Jit.Enabled;
        string jitMode = VitaSharp.Core.Cpu.Jit.Mode;
        var emu = new Emulator(new EmulatorOptions { FsRoot = root, Memory = fx.Memory, EnableJit = false, CpuBackend = "interp" });
        try
        {
            var lines = SessionReport.Lines(emu).ToList();
            Assert.StartsWith("Stopping. ", lines[0]);
            Assert.Contains(lines, l => l.StartsWith("  GPU commands: submitted ", StringComparison.Ordinal));
            Assert.Contains(lines, l => l.StartsWith("PERF cpu ", StringComparison.Ordinal));
            Assert.StartsWith("PERF allocated ", lines[^1]);
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
            try { Directory.Delete(root, true); } catch (IOException) { }
        }
    }
}
