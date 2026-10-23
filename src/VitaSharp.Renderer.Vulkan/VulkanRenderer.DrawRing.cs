using System.Diagnostics;
using System.Globalization;
using System.Text;
using VitaSharp.Core.Gpu;
using VitaSharp.Core.Gxm;
using VitaSharp.Core.Util;

namespace VitaSharp.Renderer.Vulkan;

/// <summary>How a sampled texture was resolved (set by ResolveTexture, recorded by the draw ring).</summary>
[Flags]
enum TexSource : byte
{
    None = 0,          // nothing bound (a rendered surface sampled as a cube)
    Memory = 1,        // guest memory (texture cache)
    MemoryCube = 2,    // guest memory, a 2D texture read as a cube
    Depth = 3,         // a depth/stencil target sampled as a texture
    RenderTarget = 4,  // a colour target used directly
    Region = 5,        // a region of a colour target copied out
    Reinterpret = 6,   // a colour target read with another format (alias view)
    RtMipmapped = 7,   // a colour target with generated mips
    KindMask = 0x0F,
    Failed = 0x20,     // the resolution gave no view (the dummy texture was bound)
    SceneStart = 0x40, // the target being rendered, read through its scene-start copy (own tiles)
    LodLowered = 0x80, // LOD min > 0 over a rendered surface: the smaller levels behind it were resolved
}

/// <summary>
/// Debug mode: the draw ring, a compact record of every draw and scene of the last <see cref="DrawRingFrames"/>
/// presented frames, so a capture holds the draw list of the frame where a glitch happened and not only of the frame
/// traced after it. Recording writes into preallocated struct arrays (no strings, no allocation); text is formatted
/// only when a capture writes drawring/. Frames are named by the present that displayed them, like replay/: draws
/// issued between presents #N-1 and #N are drawring/frame_N.txt and their image is replay/frame_N.png.
/// <para>Draw id: <c>_drawSerial</c>, the 1-based number of the DrawCommand within the frame (every guest draw counts,
/// including skipped ones), reset at each present; the same number the draw trace logs as <c>id=</c> and the shaders
/// get as <c>PushConstants.DrawId</c>. (present, id) identifies a draw within a session.</para>
/// The ring exists only while debug mode is on (allocated at the first present with it on, freed at the first present
/// with it off). See docs/notes/debug-mode.md.
/// </summary>
public sealed unsafe partial class VulkanRenderer
{
    /// <summary>Frames kept (the replay ring holds 127 measured frames: every replay image has its draw list).</summary>
    public const int DrawRingFrames = 128;
    // pools shared by all frames (powers of two); a frame whose records were overwritten is dropped from captures.
    // 65536 draws = 512 per frame on average (Killzone gameplay: 300-420); ~19 MB in all.
    const int DrawPool = 1 << 16, TexPool = 1 << 18, ScenePool = 1 << 13;
    // per frame, so one huge frame cannot evict the whole ring; the rest is counted as dropped
    const int MaxDrawsPerFrame = 4096, MaxTexPerFrame = 16384, MaxScenesPerFrame = 512, MaxTexPerDraw = 16;

    struct DrDraw
    {
        public int Id;
        public ushort Scene;        // index of the scene within the frame, 0xFFFF = none
        public byte Prim, Msaa;     // msaa: 0 off, 1 supersampled 2x, 2/4 native samples
        public byte Flags;          // DrVsFailed | DrFsFailed | DrHasBlend
        public byte TexCount;
        public ulong Vs, Fs, VsHint, FsHint;
        public uint RtAddr, RtFormat, DsAddr;
        public ushort RtW, RtH, DsW, DsH;
        public uint IndexCount, InstanceCount;
        public GxmBlendInfo Blend;
        public DrawState State;
        public long TexStart;       // absolute index into the texture pool
    }

    const byte DrVsFailed = 1, DrFsFailed = 2, DrHasBlend = 4;

    struct DrTex
    {
        public GxmTextureDesc Desc;
        public uint SrcAddr;        // the render/depth target it came from (0 = memory)
        public byte Unit;           // binding, | 0x80 = vertex texture
        public TexSource Src;
    }

    struct DrScene
    {
        public int FirstDrawId;
        public uint RtAddr, RtFormat, RtW, RtH, W, H;
        public uint Ds, St, DsFormat;
        public float BgDepth;
        public byte BgStencil, GxmMsaa, Msaa, Flags; // DrSc*
    }

    const byte DrScDownscale = 1, DrScLoad = 2, DrScStore = 4, DrScDepth = 8, DrScGamma = 16, DrScColor = 32;

    struct DrFrame
    {
        public long Present, GuestFrame, DrawStart, TexStart, SceneStart;
        public uint Fb;             // the presented framebuffer
        public int Draws, Texs, Scenes, TotalDraws, DroppedDraws, DroppedTex, DroppedScenes;
    }

    sealed class DrawRing
    {
        public readonly DrDraw[] Draws = new DrDraw[DrawPool];
        public readonly DrTex[] Texs = new DrTex[TexPool];
        public readonly DrScene[] Scenes = new DrScene[ScenePool];
        public readonly DrFrame[] Frames = new DrFrame[DrawRingFrames];
        public long DrawsWritten, TexWritten, ScenesWritten, FramesWritten;
        // the frame being recorded
        public long FrameDrawStart, FrameTexStart, FrameSceneStart;
        public int FrameDroppedDraws, FrameDroppedTex, FrameDroppedScenes;
        public long Cur = -1;       // absolute index of the draw receiving textures, -1 = none
        // cost: every 8th draw (and its textures) is timed
        public bool Timing;
        public long TimedTicks, TimedDraws;
    }

    DrawRing? _drawRing;
    TexSource _texSrc;
    uint _texSrcAddr;

    /// <summary>"\nPERF drawring: ..." when the ring ran this session, else empty.</summary>
    string DrawRingReport() => _drawRingFramesTotal == 0 ? "" :
        $"\nPERF drawring: {_drawRingFramesTotal} frames, {_drawRingDrawsTotal} draws recorded (~{_drawRingTicksEst * 1e6 / Stopwatch.Frequency / _drawRingFramesTotal:F1} us/frame on the GPU worker, sampled on every 8th draw)";
    long _drawRingFramesTotal, _drawRingDrawsTotal;
    double _drawRingTicksEst;

    /// <summary>Draw() after the shaders were looked up (the same draws the trace lists).</summary>
    void DrawRingDraw(DrawCommand d, VsEntry vs, FsEntry fs)
    {
        var r = _drawRing!;
        long t0 = 0;
        r.Timing = (_drawSerial & 7) == 0;
        if (r.Timing) t0 = Stopwatch.GetTimestamp();
        if (r.DrawsWritten - r.FrameDrawStart >= MaxDrawsPerFrame) { r.FrameDroppedDraws++; r.Cur = -1; return; }
        long abs = r.DrawsWritten++;
        ref var e = ref r.Draws[abs & (DrawPool - 1)];
        e.Id = _drawSerial;
        long sceneIdx = r.ScenesWritten - r.FrameSceneStart - 1;
        e.Scene = sceneIdx < 0 || !_inScene ? (ushort)0xFFFF : (ushort)Math.Min(sceneIdx, 0xFFFE);
        e.Prim = d.PrimitiveType;
        e.Msaa = (byte)(_ms ? (int)_msSamples : _ss ? 1 : 0);
        var blend = d.FragmentProgram.Blend;
        e.Flags = (byte)((vs.Failed ? DrVsFailed : 0) | (fs.Failed ? DrFsFailed : 0) | (blend.HasValue ? DrHasBlend : 0));
        e.Blend = blend.GetValueOrDefault();
        e.TexCount = 0;
        e.Vs = d.VertexProgram.Info.Program.Hash;
        e.Fs = d.FragmentProgram.Info.Program.Hash;
        e.VsHint = vs.HintKey;
        e.FsHint = fs.HintKey;
        var rt = _rt;
        e.RtAddr = rt?.Address ?? 0;
        e.RtFormat = rt?.ColorFormat ?? 0;
        e.RtW = (ushort)(rt?.Width ?? 0);
        e.RtH = (ushort)(rt?.Height ?? 0);
        var ds = _depth;
        e.DsAddr = ds?.Address ?? 0;
        e.DsW = (ushort)(ds?.Width ?? 0);
        e.DsH = (ushort)(ds?.Height ?? 0);
        e.IndexCount = d.IndexCount;
        e.InstanceCount = d.InstanceCount;
        e.State = d.State;
        e.TexStart = r.TexWritten;
        r.Cur = abs;
        if (r.Timing) { r.TimedTicks += Stopwatch.GetTimestamp() - t0; r.TimedDraws++; }
    }

    /// <summary>BuildTextureSet, per bound texture of the current draw (after ResolveTexture set _texSrc).</summary>
    void DrawRingTexture(bool vertex, int unit, GxmTextureDesc desc, bool failed)
    {
        var r = _drawRing!;
        if (r.Cur < 0) return;
        long t0 = r.Timing ? Stopwatch.GetTimestamp() : 0;
        ref var e = ref r.Draws[r.Cur & (DrawPool - 1)];
        if (e.TexCount >= MaxTexPerDraw || r.TexWritten - r.FrameTexStart >= MaxTexPerFrame) { r.FrameDroppedTex++; return; }
        ref var t = ref r.Texs[r.TexWritten++ & (TexPool - 1)];
        t.Desc = desc;
        t.SrcAddr = _texSrcAddr;
        t.Unit = (byte)(unit | (vertex ? 0x80 : 0));
        t.Src = _texSrc | (failed ? TexSource.Failed : 0);
        e.TexCount++;
        if (r.Timing) r.TimedTicks += Stopwatch.GetTimestamp() - t0;
    }

    /// <summary>BeginScene, after the MSAA path was chosen.</summary>
    void DrawRingScene(BeginSceneCommand b)
    {
        var r = _drawRing!;
        if (r.ScenesWritten - r.FrameSceneStart >= MaxScenesPerFrame) { r.FrameDroppedScenes++; return; }
        ref var s = ref r.Scenes[r.ScenesWritten++ & (ScenePool - 1)];
        var rt = _rt;
        s.FirstDrawId = _drawSerial + 1;
        s.RtAddr = rt?.Address ?? 0;
        s.RtFormat = rt?.ColorFormat ?? 0;
        s.RtW = rt?.Width ?? 0;
        s.RtH = rt?.Height ?? 0;
        s.W = b.Width;
        s.H = b.Height;
        var dsi = b.Depth;
        s.Ds = dsi?.DepthData ?? 0;
        s.St = dsi?.StencilData ?? 0;
        s.DsFormat = dsi?.Format ?? 0;
        s.BgDepth = dsi?.BackgroundDepth ?? 0;
        s.BgStencil = dsi?.BackgroundStencil ?? 0;
        s.GxmMsaa = (byte)b.Multisample;
        s.Msaa = (byte)(_ms ? (int)_msSamples : _ss ? 1 : 0);
        s.Flags = (byte)((b.Color?.Downscale == true ? DrScDownscale : 0) | (dsi?.ForceLoad == true ? DrScLoad : 0) | (dsi?.ForceStore == true ? DrScStore : 0)
            | (dsi != null ? DrScDepth : 0) | (b.Color?.GammaMode is > 0 ? DrScGamma : 0) | (b.Color != null ? DrScColor : 0));
    }

    /// <summary>Every present, before the present counter moves on: closes the frame (or creates/frees the ring when
    /// debug mode changed). With debug mode off this is a flag read and a null check.</summary>
    void DrawRingPresent(DisplayFrame frame)
    {
        if (!DebugMode.Enabled)
        {
            if (_drawRing != null) { _drawRing = null; Log.Info($"[dbg] draw ring released at present #{_presentCount}"); }
            return;
        }
        var r = _drawRing;
        if (r == null)
        {
            // recording starts with the next frame, so every recorded frame is complete
            _drawRing = r = new DrawRing();
            r.FrameDrawStart = r.FrameTexStart = r.FrameSceneStart = 0;
            Dbg($"draw ring on: last {DrawRingFrames} frames, up to {DrawPool} draws / {TexPool} texture bindings / {ScenePool} scenes " +
                $"({((long)DrawPool * sizeof(DrDraw) + (long)TexPool * sizeof(DrTex) + (long)ScenePool * sizeof(DrScene) + (1 << 19)) >> 20} MB host memory)");
            return;
        }
        ref var f = ref r.Frames[r.FramesWritten++ % DrawRingFrames];
        f = new DrFrame
        {
            Present = _presentCount + 1, GuestFrame = DebugMode.Frame, TotalDraws = _drawSerial, Fb = frame.Address,
            DrawStart = r.FrameDrawStart, Draws = (int)(r.DrawsWritten - r.FrameDrawStart),
            TexStart = r.FrameTexStart, Texs = (int)(r.TexWritten - r.FrameTexStart),
            SceneStart = r.FrameSceneStart, Scenes = (int)(r.ScenesWritten - r.FrameSceneStart),
            DroppedDraws = r.FrameDroppedDraws, DroppedTex = r.FrameDroppedTex, DroppedScenes = r.FrameDroppedScenes,
        };
        _drawRingFramesTotal++;
        _drawRingDrawsTotal += f.Draws;
        if (r.TimedDraws > 0) { _drawRingTicksEst += r.TimedTicks * (double)f.Draws / r.TimedDraws; r.TimedTicks = 0; r.TimedDraws = 0; }
        r.FrameDrawStart = r.DrawsWritten;
        r.FrameTexStart = r.TexWritten;
        r.FrameSceneStart = r.ScenesWritten;
        r.FrameDroppedDraws = r.FrameDroppedTex = r.FrameDroppedScenes = 0;
        r.Cur = -1;
    }

    // ================================================================ capture

    sealed class DrFrameShot
    {
        public DrFrame Frame;
        public DrDraw[] Draws = [];
        public DrTex[] Texs = [];
        public DrScene[] Scenes = [];
        // replay ring metrics of the same present (debug mode), when measured
        public bool HasReplay;
        public FrameMetrics Metrics;
        public FrameMetrics.Hit Hits;
    }

    /// <summary>
    /// Capture armed (GPU thread, right after SaveReplay): copies the complete frames out of the ring and writes
    /// drawring/ on a worker thread. Null when the ring is off.
    /// </summary>
    Task<int>? SaveDrawRing(string dir, long trigger)
    {
        if (_drawRing is not { } r) return null;
        var shots = new List<DrFrameShot>(DrawRingFrames);
        int evicted = 0;
        Dictionary<long, (FrameMetrics, FrameMetrics.Hit)>? replay = null;
        if (_replay is { } ring)
        {
            replay = new Dictionary<long, (FrameMetrics, FrameMetrics.Hit)>(ReplaySlots);
            foreach (var e in ring.Entries) if (e.Ready) replay[e.Present] = (e.Metrics, e.Hits);
        }
        for (long k = Math.Max(0, r.FramesWritten - DrawRingFrames); k < r.FramesWritten; k++)
        {
            var f = r.Frames[k % DrawRingFrames];
            if (f.DrawStart < r.DrawsWritten - DrawPool || f.TexStart < r.TexWritten - TexPool || f.SceneStart < r.ScenesWritten - ScenePool) { evicted++; continue; }
            var s = new DrFrameShot { Frame = f, Draws = new DrDraw[f.Draws], Texs = new DrTex[f.Texs], Scenes = new DrScene[f.Scenes] };
            for (int i = 0; i < f.Draws; i++) s.Draws[i] = r.Draws[(f.DrawStart + i) & (DrawPool - 1)];
            for (int i = 0; i < f.Texs; i++) s.Texs[i] = r.Texs[(f.TexStart + i) & (TexPool - 1)];
            for (int i = 0; i < f.Scenes; i++) s.Scenes[i] = r.Scenes[(f.SceneStart + i) & (ScenePool - 1)];
            if (replay != null && replay.TryGetValue(f.Present, out var m)) { s.HasReplay = true; (s.Metrics, s.Hits) = m; }
            shots.Add(s);
        }
        Log.Info($"[diag] draw ring: {shots.Count} frames (presents #{(shots.Count > 0 ? shots[0].Frame.Present : 0)}-#{(shots.Count > 0 ? shots[^1].Frame.Present : 0)}){(evicted > 0 ? $", {evicted} overwritten" : "")}, writing in the background");
        long armed = _presentCount;
        return Task.Run(() => WriteDrawRing(Path.Combine(dir, "drawring"), shots, trigger, armed, evicted));
    }

    // ---------------------------------------------------------------- per-frame summary used by the index and the diff

    sealed class DrFrameInfo
    {
        public readonly Dictionary<(ulong Vs, ulong Fs), int> Pairs = new();
        public readonly HashSet<ulong> Vs = new(), Fs = new();
        public readonly List<(uint Addr, uint Fmt, uint W, uint H)> Targets = new();
        /// <summary>texture (address, format) -> how it was resolved (first binding in the frame) and the first draw id.</summary>
        public readonly Dictionary<(uint Addr, uint Fmt), (TexSource Src, uint SrcAddr, int Draw)> TexSrc = new();
    }

    static DrFrameInfo Summarize(DrFrameShot s)
    {
        var info = new DrFrameInfo();
        var seenRt = new HashSet<(uint, uint)>();
        foreach (var sc in s.Scenes)
            if ((sc.Flags & DrScColor) != 0 && seenRt.Add((sc.RtAddr, sc.RtFormat))) info.Targets.Add((sc.RtAddr, sc.RtFormat, sc.RtW, sc.RtH));
        long texBase = s.Frame.TexStart;
        foreach (var d in s.Draws)
        {
            info.Pairs[(d.Vs, d.Fs)] = info.Pairs.GetValueOrDefault((d.Vs, d.Fs)) + 1;
            info.Vs.Add(d.Vs);
            info.Fs.Add(d.Fs);
            for (int i = 0; i < d.TexCount; i++)
            {
                long ti = d.TexStart - texBase + i;
                if (ti < 0 || ti >= s.Texs.Length) break;
                var t = s.Texs[ti];
                info.TexSrc.TryAdd((t.Desc.DataAddress, t.Desc.Format), (t.Src & ~TexSource.Failed, t.SrcAddr, d.Id));
            }
        }
        return info;
    }

    /// <summary>Lines of the difference of <paramref name="cur"/> against <paramref name="prev"/> (empty = same programs,
    /// targets and texture sources); <paramref name="structural"/> = programs, targets or texture sources differ.</summary>
    static List<string> Diff(DrFrameShot prevShot, DrFrameInfo prev, DrFrameShot curShot, DrFrameInfo cur, out bool structural)
    {
        var lines = new List<string>();
        int dd = curShot.Draws.Length - prevShot.Draws.Length, ds = curShot.Scenes.Length - prevShot.Scenes.Length;
        var addedFs = cur.Fs.Where(h => !prev.Fs.Contains(h)).ToList();
        var removedFs = prev.Fs.Where(h => !cur.Fs.Contains(h)).ToList();
        var addedVs = cur.Vs.Where(h => !prev.Vs.Contains(h)).ToList();
        var removedVs = prev.Vs.Where(h => !cur.Vs.Contains(h)).ToList();
        // the presented framebuffers are left out: double/triple buffering swaps them every frame, in any order
        uint fbA = curShot.Frame.Fb, fbB = prevShot.Frame.Fb;
        var addedRt = cur.Targets.Where(t => t.Addr != fbA && t.Addr != fbB && !prev.Targets.Any(p => p.Addr == t.Addr && p.Fmt == t.Fmt)).ToList();
        var removedRt = prev.Targets.Where(t => t.Addr != fbA && t.Addr != fbB && !cur.Targets.Any(p => p.Addr == t.Addr && p.Fmt == t.Fmt)).ToList();
        var switched = new List<string>();
        foreach (var (key, c) in cur.TexSrc)
            if (prev.TexSrc.TryGetValue(key, out var p) && (p.Src != c.Src || p.SrcAddr != c.SrcAddr))
                switched.Add($"0x{key.Addr:X8}:{key.Fmt:X8} {SrcName(p.Src, p.SrcAddr)} -> {SrcName(c.Src, c.SrcAddr)} (first draw id={c.Draw})");
        int texNew = cur.TexSrc.Keys.Count(k => !prev.TexSrc.ContainsKey(k)), texGone = prev.TexSrc.Keys.Count(k => !cur.TexSrc.ContainsKey(k));
        structural = addedFs.Count + removedFs.Count + addedVs.Count + removedVs.Count + addedRt.Count + removedRt.Count + switched.Count > 0;

        lines.Add($"draws {curShot.Draws.Length} ({dd:+0;-0;=}), scenes {curShot.Scenes.Length} ({ds:+0;-0;=}), textures {cur.TexSrc.Count} (+{texNew} -{texGone}), framebuffer 0x{fbA:X8}{(fbA != fbB ? $" (was 0x{fbB:X8})" : "")}");
        void Programs(string what, List<ulong> list, DrFrameInfo inFrame, bool fs)
        {
            if (list.Count == 0) return;
            lines.Add($"{what}: " + string.Join(", ", list.Take(12).Select(h => $"{h:X16}({inFrame.Pairs.Where(kv => (fs ? kv.Key.Fs : kv.Key.Vs) == h).Sum(kv => kv.Value)} draws)"))
                + (list.Count > 12 ? $", +{list.Count - 12} more" : ""));
        }
        Programs("fs added", addedFs, cur, true);
        Programs("fs removed", removedFs, prev, true);
        Programs("vs added", addedVs, cur, false);
        Programs("vs removed", removedVs, prev, false);
        // program pairs whose draw count changed the most (dynamic geometry; informative, not structural)
        var changed = cur.Pairs.Select(kv => (kv.Key, Delta: kv.Value - prev.Pairs.GetValueOrDefault(kv.Key)))
            .Concat(prev.Pairs.Where(kv => !cur.Pairs.ContainsKey(kv.Key)).Select(kv => (kv.Key, Delta: -kv.Value)))
            .Where(x => x.Delta != 0).OrderByDescending(x => Math.Abs(x.Delta)).Take(6).ToList();
        if (changed.Count > 0) lines.Add("draw count by program: " + string.Join(", ", changed.Select(x => $"fs {x.Key.Fs:X16}/vs {x.Key.Vs:X16} {x.Delta:+0;-0}")));
        if (addedRt.Count > 0) lines.Add("targets added: " + string.Join(", ", addedRt.Select(t => $"0x{t.Addr:X8}:{t.Fmt:X8} {t.W}x{t.H}")));
        if (removedRt.Count > 0) lines.Add("targets removed: " + string.Join(", ", removedRt.Select(t => $"0x{t.Addr:X8}:{t.Fmt:X8} {t.W}x{t.H}")));
        if (switched.Count > 0) lines.Add($"textures switched source ({switched.Count}): " + string.Join("; ", switched.Take(16)) + (switched.Count > 16 ? $"; +{switched.Count - 16} more" : ""));
        return lines;
    }

    static string SrcName(TexSource src, uint addr)
    {
        string kind = (src & TexSource.KindMask) switch
        {
            TexSource.Memory => "mem",
            TexSource.MemoryCube => "memcube",
            TexSource.Depth => "depth",
            TexSource.RenderTarget => "rt",
            TexSource.Region => "region",
            TexSource.Reinterpret => "reinterp",
            TexSource.RtMipmapped => "rtmip",
            _ => "none",
        };
        if (addr != 0) kind += $"@0x{addr:X8}";
        if ((src & TexSource.SceneStart) != 0) kind += "+scenestart";
        if ((src & TexSource.LodLowered) != 0) kind += "+lodmin";
        if ((src & TexSource.Failed) != 0) kind += "+FAILED";
        return kind;
    }

    static string MsaaName(byte m) => m switch { 0 => "off", 1 => "ss2x", _ => $"{m}x" };

    static int WriteDrawRing(string dir, List<DrFrameShot> shots, long trigger, long armed, int evicted)
    {
        var inv = CultureInfo.InvariantCulture;
        Directory.CreateDirectory(dir);
        var infos = shots.Select(Summarize).ToList();
        var diffs = new List<string>[shots.Count];
        var structural = new bool[shots.Count];
        for (int i = 0; i < shots.Count; i++)
            diffs[i] = i == 0 ? ["(first frame in the ring)"] : Diff(shots[i - 1], infos[i - 1], shots[i], infos[i], out structural[i]);
        // transient: differs from both neighbours while they match each other (a one-frame glitch stands out), and
        // also from the frames two away, so double-buffered A/B/A/B framebuffers or alternating passes do not count
        var transient = new bool[shots.Count];
        bool Differs(int a, int b)
        {
            if (a < 0 || b < 0 || a >= shots.Count || b >= shots.Count) return true;
            Diff(shots[a], infos[a], shots[b], infos[b], out bool d);
            return d;
        }
        for (int i = 1; i + 1 < shots.Count; i++)
        {
            if (!structural[i] || !structural[i + 1] || Differs(i - 1, i + 1)) continue;
            transient[i] = Differs(i - 2, i) && Differs(i, i + 2);
        }

        var index = new StringBuilder();
        index.AppendLine("# VitaSharp draw ring (debug mode): the draw list of each of the last presented frames before the capture");
        index.AppendLine($"# {shots.Count} frames{(evicted > 0 ? $" ({evicted} older frames were overwritten: more draws than the pools hold)" : "")}; trigger present #{trigger}; capture armed at present #{armed} (draws.txt = the frame after it)");
        index.AppendLine("# frame_<present>.txt = draws issued between presents #<present-1> and #<present>; the image is replay/frame_<present>.png");
        index.AppendLine("# draws = recorded/issued (issued counts every guest draw command; skipped ones are not recorded), drop = draws/textures over the per-frame cap");
        index.AppendLine("# new = programs not drawn in the previous frame; rts = colour targets of the frame's scenes; replay = mean brightness and per-frame hits of replay/");
        index.AppendLine("# replay=- on the last frame: its image was still on the GPU when the capture was armed (screenshot.png/draws.txt are the frame after it)");
        index.AppendLine("# TRANSIENT = programs/targets/texture sources differ from both neighbours (which match each other) and from the frames two away");
        index.AppendLine("# present guestFrame draws scenes textures drop new rts fb(presented framebuffer, ignored by the diff) replay");
        for (int i = 0; i < shots.Count; i++)
        {
            var s = shots[i];
            var f = s.Frame;
            var info = infos[i];
            string news = "-";
            if (i > 0)
            {
                var prev = infos[i - 1];
                var nf = info.Fs.Where(h => !prev.Fs.Contains(h)).Select(h => $"fs:{h:X16}").Concat(info.Vs.Where(h => !prev.Vs.Contains(h)).Select(h => $"vs:{h:X16}")).ToList();
                if (nf.Count > 0) news = string.Join(",", nf.Take(8)) + (nf.Count > 8 ? $",+{nf.Count - 8}" : "");
            }
            string rts = info.Targets.Count == 0 ? "-" : string.Join(",", info.Targets.Take(16).Select(t => $"0x{t.Addr:X8}")) + (info.Targets.Count > 16 ? $",+{info.Targets.Count - 16}" : "");
            string rep = s.HasReplay ? string.Create(inv, $"mean={s.Metrics.Mean:F3} hits={HitString(s.Hits)}") : "-";
            string drop = f.DroppedDraws + f.DroppedTex + f.DroppedScenes == 0 ? "0" : $"{f.DroppedDraws}d/{f.DroppedTex}t/{f.DroppedScenes}s";
            index.AppendLine($"p{f.Present} f{f.GuestFrame} draws={f.Draws}/{f.TotalDraws} scenes={f.Scenes} tex={s.Texs.Length} drop={drop} new={news} rts={rts} fb=0x{f.Fb:X8} replay={rep}"
                + (transient[i] ? "  TRANSIENT" : "") + (f.Present == trigger ? "  <== trigger" : ""));
        }
        File.WriteAllText(Path.Combine(dir, "index.txt"), index.ToString());

        var diff = new StringBuilder();
        diff.AppendLine("# VitaSharp draw ring: each frame against the previous one (programs, targets, textures that switched source)");
        diff.AppendLine("# texture sources: mem = guest memory, rt@A = colour target A used directly, region@A = a region of A, reinterp@A = A read with another format, rtmip@A = A with generated mips, depth@A = depth target A; +scenestart = the target being rendered, read through its scene-start copy");
        for (int i = 0; i < shots.Count; i++)
        {
            var f = shots[i].Frame;
            diff.AppendLine($"p{f.Present} f{f.GuestFrame}{(i > 0 ? $" vs p{shots[i - 1].Frame.Present}" : "")}{(transient[i] ? "  TRANSIENT" : "")}{(f.Present == trigger ? "  <== trigger" : "")}{(i > 0 && !structural[i] ? "  (same programs, targets and texture sources)" : "")}");
            foreach (var l in diffs[i]) diff.AppendLine("  " + l);
        }
        File.WriteAllText(Path.Combine(dir, "diff.txt"), diff.ToString());

        var sb = new StringBuilder();
        for (int i = 0; i < shots.Count; i++)
        {
            sb.Clear();
            var s = shots[i];
            var f = s.Frame;
            sb.AppendLine($"# VitaSharp draw ring: the frame between presents #{f.Present - 1} and #{f.Present} (guest frame {f.GuestFrame}); its image is replay/frame_{f.Present:D6}.png{(f.Present == trigger ? "  <== trigger" : "")}{(transient[i] ? "  TRANSIENT" : "")}");
            sb.AppendLine($"# {f.Draws} draws recorded of {f.TotalDraws} issued, {f.Scenes} scenes, {s.Texs.Length} texture bindings{(f.DroppedDraws + f.DroppedTex + f.DroppedScenes > 0 ? $"; dropped over the per-frame cap: {f.DroppedDraws} draws, {f.DroppedTex} textures, {f.DroppedScenes} scenes" : "")}");
            sb.AppendLine("# same format as draws.txt minus vertex data and uniforms (a[], st[], v0/v1, vu/fu, vuf/fuh); extra fields: vh/fh = hint keys (shaders/<hash>_<hint>),");
            sb.AppendLine("# scene = scene index in this file, tN/vtN ...:Z<swizzle>:K<source> (see diff.txt), rtsize; [scene] adds fmt, rtsize, st, dsfmt, store, bgStencil, gamma, path");
            sb.AppendLine($"# diff vs previous frame{(i > 0 ? $" (p{shots[i - 1].Frame.Present})" : "")}:");
            foreach (var l in diffs[i]) sb.AppendLine("#   " + l);
            int sc = 0;
            long texBase = f.TexStart;
            foreach (var d in s.Draws)
            {
                // the scenes that began before this draw
                while (sc < s.Scenes.Length && s.Scenes[sc].FirstDrawId <= d.Id) AppendScene(sb, inv, s.Scenes[sc++], f.Present - 1);
                AppendDraw(sb, inv, d, s, texBase, f.Present - 1);
            }
            while (sc < s.Scenes.Length) AppendScene(sb, inv, s.Scenes[sc++], f.Present - 1);
            File.WriteAllText(Path.Combine(dir, $"frame_{f.Present:D6}.txt"), sb.ToString());
        }
        Log.Info($"[diag] draw ring written: {shots.Count} frames into {dir}");
        return shots.Count;
    }

    static void AppendScene(StringBuilder sb, CultureInfo inv, in DrScene s, long present)
    {
        bool depth = (s.Flags & DrScDepth) != 0;
        sb.Append(inv, $"[scene] begin present={present} rt=0x{s.RtAddr:X8} {s.W}x{s.H} msaa={s.GxmMsaa} downscale={((s.Flags & DrScColor) != 0 ? ((s.Flags & DrScDownscale) != 0).ToString() : "")} ds=0x{s.Ds:X8}");
        sb.Append(inv, $" load={(depth ? ((s.Flags & DrScLoad) != 0).ToString() : "")} bgDepth={(depth ? s.BgDepth.ToString(inv) : "")} (next draw id={s.FirstDrawId})");
        sb.Append(inv, $" fmt=0x{s.RtFormat:X8} rtsize={s.RtW}x{s.RtH} st=0x{s.St:X8} dsfmt=0x{s.DsFormat:X8} store={(s.Flags & DrScStore) != 0} bgStencil={s.BgStencil} gamma={(s.Flags & DrScGamma) != 0} path={MsaaName(s.Msaa)}\n");
    }

    static void AppendDraw(StringBuilder sb, CultureInfo inv, in DrDraw d, DrFrameShot s, long texBase, long present)
    {
        var st = d.State;
        sb.Append(inv, $"[draw] id={d.Id} f={present} rt=0x{d.RtAddr:X8}:{d.RtFormat:X8} ds=0x{d.DsAddr:X8}/{d.DsW}x{d.DsH} vs={d.Vs:X16} fs={d.Fs:X16} prim={d.Prim} n={d.IndexCount}x{d.InstanceCount}");
        var b = d.Blend;
        sb.Append(inv, $" blend={((d.Flags & DrHasBlend) != 0 ? $"{b.ColorMask:X}:{b.ColorFunc}/{b.ColorSrc}/{b.ColorDst}" : "none")} fragEn={st.Front.FragmentEnabled} cull={st.CullMode} twoSided={st.TwoSided} depth={st.Front.DepthFunc}/{st.Front.DepthWrite} bias={st.Front.DepthBiasFactor}/{st.Front.DepthBiasUnits} st={st.Front.Stencil.Func}/{st.Back.Stencil.Func}");
        sb.Append(inv, $" vp={st.ViewportEnabled}({st.VpOffX},{st.VpOffY},{st.VpScaleX},{st.VpScaleY},{st.VpOffZ},{st.VpScaleZ}) clip={st.RegionClipMode}[{st.ClipMinX},{st.ClipMinY}-{st.ClipMaxX},{st.ClipMaxY}]");
        for (int i = 0; i < d.TexCount; i++)
        {
            long ti = d.TexStart - texBase + i;
            if (ti < 0 || ti >= s.Texs.Length) break;
            var t = s.Texs[ti];
            var x = t.Desc;
            sb.Append(inv, $" {((t.Unit & 0x80) != 0 ? "vt" : "t")}{t.Unit & 0x7F}=0x{x.DataAddress:X8}:{x.Format:X8}:{x.Width}x{x.Height}:T{x.TextureType >> 29}:S{x.StrideBytes}:G{x.GammaMode}:F{x.MagFilter}{x.MinFilter}{x.MipFilter}:B{x.LodBias}:L{x.LodMin}:M{x.MipCount}:A{x.UAddr}{x.VAddr}:W0={x.W0:X8}:Z{x.Swizzle}:K{SrcName(t.Src, t.SrcAddr)}");
        }
        sb.Append(inv, $" vh={d.VsHint:X16} fh={d.FsHint:X16} scene={(d.Scene == 0xFFFF ? "-" : d.Scene.ToString(inv))} rtsize={d.RtW}x{d.RtH}");
        if ((d.Flags & (DrVsFailed | DrFsFailed)) != 0) sb.Append($" FAILED={((d.Flags & DrVsFailed) != 0 ? "vs" : "")}{((d.Flags & DrFsFailed) != 0 ? "fs" : "")}");
        sb.Append(inv, $" msaa={MsaaName(d.Msaa)}\n");
    }
}
