using VitaSharp.Core.Hle.Audio;
using VitaSharp.Core.Memory;
using VitaSharp.Core.Util;

namespace VitaSharp.Core.Hle.Modules;

/// <summary>
/// <c>VITASHARP_NGS_TRACE=1</c>: a line per source voice event (play, key off, kill, buffer callbacks, buffer table
/// changes) with the voice's tick, its reported position and its buffer table, so streamed voices (a refill that
/// lands late, a buffer played twice) can be followed. With <c>--audio-dump</c> each source voice's output is also
/// written to its own WAV (only the ticks it is queued, so silence between plays is not kept).
/// </summary>
public sealed partial class Ngs
{
    static readonly bool Trace = Environment.GetEnvironmentVariable(EnvVars.NgsTrace) == "1";
    [VitaSharp.Core.SaveState.SnapshotIgnore] readonly Dictionary<uint, (AudioDump.Writer? Writer, int Generation)> _voiceDumps = new();
    [VitaSharp.Core.SaveState.SnapshotIgnore] short[] _voiceDumpScratch = [];

    static NgsModuleData? SourceModule(NgsVoice v)
    {
        foreach (var m in v.Modules) if (m.Source != null) return m;
        return null;
    }

    static string DescribeSource(NgsModuleData m)
    {
        bool player = m.Kind == NgsModuleKind.Player;
        int stride = player ? PlayerLayout(m).Stride : 16;
        var sb = new System.Text.StringBuilder();
        sb.Append($"cur {GetState(m, 4)} pos {GetState(m, 0)} samples {GetState(m, 8)} bytes {GetState(m, 12)} avail {m.Source!.Available} loop {m.Source.LoopCount}");
        if (player)
        {
            var pl = PlayerLayout(m);
            sb.Append($" freq {m.ParamF(pl.Freq)}x{m.ParamF(pl.Freq + 4)} ch {m.ParamI8(pl.Channels)} type {m.ParamI8(pl.Type)}");
        }
        else sb.Append($" freq {m.ParamF(72)}x{m.ParamF(76)} cfg 0x{m.Param32(92):X8}");
        sb.Append(" bufs");
        for (int i = 0; i < 4; i++)
        {
            int bp = 8 + i * stride;
            sb.Append($" [{i}] 0x{m.Param32(bp):X8}:{m.Param32(bp + 4)} l{m.ParamI16(bp + 8)} n{m.ParamI16(bp + 10)}");
        }
        return sb.ToString();
    }

    void TraceVoice(NgsVoice v, string what)
    {
        if (!Trace) return;
        var m = SourceModule(v);
        if (m == null) return;
        Log.Info($"[ngs] voice 0x{v.Handle:X8} {v.Rack.Type} t{v.FrameCount} {what}: {DescribeSource(m)}");
        if (what != "play") return;
        foreach (var md in v.Modules)
            if (md.Source == null && md.ParamAddr != 0)
                Log.Info($"[ngs]   module {md.Index} {md.Kind}{(md.Bypassed ? " (bypassed)" : "")}: {Convert.ToHexString(GuestMemory.Span(md.ParamAddr, (int)md.ParamSize))}");
    }

    static unsafe string CallerInfo()
    {
        if (!Trace || Kernel.GuestThread.Current is not { } t) return "?";
        var s = t.Core.State;
        return $"[{t.Name}] lr 0x{s->R[14]:X8} {t.Backtrace(s)}";
    }

    /// <summary>Hashes of the 8 eighths of a looping buffer (which parts the game rewrote since the last lap).</summary>
    static string RingHashes(uint buf, int bytes)
    {
        var sb = new System.Text.StringBuilder();
        int part = bytes / 8;
        for (int i = 0; i < 8 && part > 0; i++)
            sb.Append((System.IO.Hashing.XxHash32.HashToUInt32(GuestMemory.Span(buf + (uint)(i * part), part)) & 0xFFFF).ToString("X4")).Append(' ');
        return sb.ToString();
    }

    void DumpVoice(NgsVoice v)
    {
        // per-voice WAVs: VITASHARP_NGS_TRACE with --audio-dump, or a session capture (AudioDump.VoiceDumps)
        if (!(Trace || AudioDump.VoiceDumps) || !AudioDump.Enabled || v.Product == null || SourceModule(v) == null) return;
        AudioDump.Writer? w;
        if (!_voiceDumps.TryGetValue(v.Handle, out var e) || e.Generation != AudioDump.Generation)
            _voiceDumps[v.Handle] = (w = AudioDump.Open($"ngs_voice_{v.Handle:X8}", v.Rack.System.SampleRate), AudioDump.Generation);
        else w = e.Writer;
        if (w == null) return;
        int n = v.Rack.System.Granularity * 2;
        if (_voiceDumpScratch.Length < n) _voiceDumpScratch = new short[n];
        for (int i = 0; i < n; i++) _voiceDumpScratch[i] = (short)Math.Clamp(v.Product[i] * 32768f, -32768f, 32767f);
        w.Write(_voiceDumpScratch.AsSpan(0, n));
    }
}
