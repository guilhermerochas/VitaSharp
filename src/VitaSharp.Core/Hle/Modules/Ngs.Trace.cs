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
        TraceRoute(v, "  ", 0);
        foreach (var md in v.Modules)
            if (md.Source == null && md.ParamAddr != 0)
                Log.Info($"[ngs]   module {md.Index} {md.Kind}{(md.Bypassed ? " (bypassed)" : "")}: {Convert.ToHexString(GuestMemory.Span(md.ParamAddr, (int)md.ParamSize))}");
    }

    /// <summary>The voice's output patches and, down to the master, each destination's (its rack, modules and volumes).</summary>
    static void TraceRoute(NgsVoice v, string indent, int depth)
    {
        if (depth > 6) return;
        for (int o = 0; o < v.Patches.Length; o++)
            foreach (var p in v.Patches[o])
            {
                if (p is not { Active: true }) continue;
                var d = p.Dest;
                Log.Info($"[ngs] {indent}output {o} -> voice 0x{d.Handle:X8} {d.Rack.Type} ({d.Rack.ChannelsPerVoice} ch, {d.State}{(d.Paused ? ", paused" : "")}) input {p.DestIndex} volume {p.Volume[0, 0]:F3} {p.Volume[0, 1]:F3} {p.Volume[1, 0]:F3} {p.Volume[1, 1]:F3}");
                foreach (var md in d.Modules)
                    if (md.ParamAddr != 0 && md.ParamSize > 0 && d.Rack.Type != NgsBuss.Atrac9)
                        Log.Info($"[ngs] {indent}  dest module {md.Index} {md.Kind}{(md.Bypassed ? " (bypassed)" : "")}: {Convert.ToHexString(GuestMemory.Span(md.ParamAddr, (int)md.ParamSize))}");
                TraceRoute(d, indent + "  ", depth + 1);
            }
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

    [VitaSharp.Core.SaveState.SnapshotIgnore] readonly Dictionary<NgsModuleData, uint[]> _ringHashes = new();

    /// <summary>Which superframes of a looping ring the game rewrote since the last tick, and where we were reading.</summary>
    void TraceRingWrites(NgsVoice v, NgsModuleData m, uint buf, int bytes, int sfBytes)
    {
        if (!Trace || sfBytes <= 0 || bytes <= 0 || bytes > 1 << 16) return;
        int n = bytes / sfBytes;
        bool fresh = !_ringHashes.TryGetValue(m, out var h) || h.Length != n + 1 || h[n] != buf;
        if (fresh) { h = new uint[n + 1]; h[n] = buf; _ringHashes[m] = h; }
        var sb = fresh ? null : new System.Text.StringBuilder();
        int runStart = -1, zeros = 0;
        for (int i = 0; i <= n; i++)
        {
            bool changed = false;
            if (i < n)
            {
                var span = GuestMemory.Span(buf + (uint)(i * sfBytes), sfBytes);
                uint x = System.IO.Hashing.XxHash32.HashToUInt32(span);
                changed = !fresh && x != h![i];
                if (changed && !span.ContainsAnyExcept((byte)0)) zeros++;
                h![i] = x;
            }
            if (changed && runStart < 0) runStart = i;
            if (!changed && runStart >= 0) { sb!.Append($" {runStart}-{i - 1}"); runStart = -1; }
        }
        if (sb is { Length: > 0 })
            Log.Info($"[ngs] voice 0x{v.Handle:X8} t{v.FrameCount} pos {GetState(m, 0)} (sf {GetState(m, 0) / sfBytes} of {n}) rewrote sf{sb}{(zeros > 0 ? $" ({zeros} zero)" : "")}");
    }

    void TraceStateRead(NgsModuleData m)
    {
        if (!Trace) return;
        if (m.Source == null || m.GuestState.Length < 16)
        {
            Log.Info($"[ngs] voice 0x{m.Voice.Handle:X8} t{m.Voice.FrameCount} state read of module {m.Index} {m.Kind} ({m.GuestState.Length} bytes kept) by {CallerInfo()}");
            return;
        }
        Log.Info($"[ngs] voice 0x{m.Voice.Handle:X8} t{m.Voice.FrameCount} state read module {m.Index} pos {GetState(m, 0)} buf {GetState(m, 4)} samples {GetState(m, 8)} bytes {GetState(m, 12)} by {CallerInfo()}");
    }

    [VitaSharp.Core.SaveState.SnapshotIgnore] readonly Dictionary<uint, FileStream?> _rawDumps = new();

    /// <summary>Appends a superframe as decoded to <c>ngs_voice_X_cfgC.at9raw</c> (compare with another decoder).</summary>
    void DumpSuperframe(NgsVoice v, uint config, ReadOnlySpan<byte> sf)
    {
        if (!Trace || !AudioDump.Enabled) return;
        if (!_rawDumps.TryGetValue(v.Handle, out var f))
            _rawDumps[v.Handle] = f = AudioDump.OpenRaw($"ngs_voice_{v.Handle:X8}_cfg{config:X8}.at9raw");
        if (f == null) return;
        f.Write(sf);
        f.Flush();
    }

    sealed class StaleStats { public uint Buf; public uint[] Hash = []; public int Decoded, Stale, Run, Longest, Zero; }
    [VitaSharp.Core.SaveState.SnapshotIgnore] readonly Dictionary<NgsModuleData, StaleStats> _stale = new();

    /// <summary>Counts superframes decoded again from a ring slot the game did not rewrite since the last decode there
    /// (stale data played twice), per voice play; summarised at stop.</summary>
    void TraceStale(NgsModuleData m, uint buf, int index, ReadOnlySpan<byte> sf)
    {
        if (!Trace || index < 0 || index > 4096) return;
        if (!_stale.TryGetValue(m, out var s)) _stale[m] = s = new StaleStats();
        if (s.Buf != buf) { s.Buf = buf; s.Hash = new uint[4097]; }
        uint h = System.IO.Hashing.XxHash32.HashToUInt32(sf) | 1;
        s.Decoded++;
        if (!sf.ContainsAnyExcept((byte)0)) { s.Zero++; s.Run = 0; }
        else if (s.Hash[index] == h) { s.Stale++; s.Run++; s.Longest = Math.Max(s.Longest, s.Run); }
        else s.Run = 0;
        s.Hash[index] = h;
    }

    void TraceStaleSummary(NgsVoice v)
    {
        if (!Trace || SourceModule(v) is not { } m || !_stale.TryGetValue(m, out var s) || s.Decoded == 0) return;
        Log.Info($"[ngs] voice 0x{v.Handle:X8} t{v.FrameCount} stale superframes {s.Stale} of {s.Decoded} (longest run {s.Longest}, zero {s.Zero})");
        s.Decoded = s.Stale = s.Run = s.Longest = s.Zero = 0;
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
