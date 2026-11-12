using System.Diagnostics;
using System.Globalization;
using System.Text;
using VitaSharp.Core.Gpu;
using VitaSharp.Core.Gxm;
using VitaSharp.Core.Util;

namespace VitaSharp.Renderer.Vulkan;

/// <summary>How a sampled texture was resolved (set by ResolveTexture, recorded by the draw ring).</summary>
[Flags]
internal enum TexSource : byte
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
    Memo = 0x10,       // (worker profile only) the unit came from the resolved-unit memo
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
    private const int DrawPool = 1 << 16, TexPool = 1 << 18, ScenePool = 1 << 13;
    // per frame, so one huge frame cannot evict the whole ring; the rest is counted as dropped
    private const int MaxDrawsPerFrame = 4096, MaxTexPerFrame = 16384, MaxScenesPerFrame = 512, MaxTexPerDraw = 16;

    private struct DrDraw
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

    private const byte DrVsFailed = 1, DrFsFailed = 2, DrHasBlend = 4;

    private struct DrTex
    {
        public GxmTextureDesc Desc;
        public uint SrcAddr;        // the render/depth target it came from (0 = memory)
        public byte Unit;           // binding, | 0x80 = vertex texture
        public TexSource Src;
    }

    private struct DrScene
    {
        public int FirstDrawId;
        public uint RtAddr, RtFormat, RtW, RtH, W, H;
        public uint Ds, St, DsFormat;
        public float BgDepth;
        public byte BgStencil, GxmMsaa, Msaa, Flags; // DrSc*
    }

    private const byte DrScDownscale = 1, DrScLoad = 2, DrScStore = 4, DrScDepth = 8, DrScGamma = 16, DrScColor = 32;

    private struct DrFrame
    {
        public long Present, GuestFrame, DrawStart, TexStart, SceneStart;
        public uint Fb;             // the presented framebuffer
        public int Draws, Texs, Scenes, TotalDraws, DroppedDraws, DroppedTex, DroppedScenes;
    }

    private sealed class DrawRing
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

    private DrawRing? _drawRing;
    private TexSource _texSrc;
    private uint _texSrcAddr;

    /// <summary>"\nPERF drawring: ..." when the ring ran this session, else empty.</summary>
    private string DrawRingReport() => _drawRingFramesTotal == 0 ? "" :
        $"\nPERF drawring: {_drawRingFramesTotal} frames, {_drawRingDrawsTotal} draws recorded (~{_drawRingTicksEst * 1e6 / Stopwatch.Frequency / _drawRingFramesTotal:F1} us/frame on the GPU worker, sampled on every 8th draw)";
    private long _drawRingFramesTotal, _drawRingDrawsTotal;
    private double _drawRingTicksEst;

    /// <summary>Draw() after the shaders were looked up (the same draws the trace lists).</summary>
    private void DrawRingDraw(DrawCommand d, VsEntry vs, FsEntry fs)
    {
        DrawRing r = _drawRing!;
        long t0 = 0;
        r.Timing = (_drawSerial & 7) == 0;
        if (r.Timing)
        {
            t0 = Stopwatch.GetTimestamp();
        }

        if (r.DrawsWritten - r.FrameDrawStart >= MaxDrawsPerFrame)
        {
            r.FrameDroppedDraws++;
            r.Cur = -1;
            return;
        }
        long abs = r.DrawsWritten++;
        ref DrDraw e = ref r.Draws[abs & (DrawPool - 1)];
        e.Id = _drawSerial;
        long sceneIdx = r.ScenesWritten - r.FrameSceneStart - 1;
        e.Scene = sceneIdx < 0 || !_inScene ? (ushort)0xFFFF : (ushort)Math.Min(sceneIdx, 0xFFFE);
        e.Prim = d.PrimitiveType;
        e.Msaa = (byte)(_multisampled ? (int)_msSamples : _supersampled ? 1 : 0);
        GxmBlendInfo? blend = d.FragmentProgram.Blend;
        e.Flags = (byte)((vs.Failed ? DrVsFailed : 0) | (fs.Failed ? DrFsFailed : 0) | (blend.HasValue ? DrHasBlend : 0));
        e.Blend = blend.GetValueOrDefault();
        e.TexCount = 0;
        e.Vs = d.VertexProgram.Info.Program.Hash;
        e.Fs = d.FragmentProgram.Info.Program.Hash;
        e.VsHint = vs.HintKey;
        e.FsHint = fs.HintKey;
        RenderTarget? rt = _renderTarget;
        e.RtAddr = rt?.Address ?? 0;
        e.RtFormat = rt?.ColorFormat ?? 0;
        e.RtW = (ushort)(rt?.Width ?? 0);
        e.RtH = (ushort)(rt?.Height ?? 0);
        DepthTarget? depthTarget = _depth;
        e.DsAddr = depthTarget?.Address ?? 0;
        e.DsW = (ushort)(depthTarget?.Width ?? 0);
        e.DsH = (ushort)(depthTarget?.Height ?? 0);
        e.IndexCount = d.IndexCount;
        e.InstanceCount = d.InstanceCount;
        e.State = d.State;
        e.TexStart = r.TexWritten;
        r.Cur = abs;
        if (r.Timing)
        {
            r.TimedTicks += Stopwatch.GetTimestamp() - t0;
            r.TimedDraws++;
        }
    }

    /// <summary>BuildTextureSet, per bound texture of the current draw (after ResolveTexture set _texSrc).</summary>
    private void DrawRingTexture(bool vertex, int unit, GxmTextureDesc desc, bool failed)
    {
        DrawRing r = _drawRing!;
        if (r.Cur < 0)
        {
            return;
        }

        long t0 = r.Timing ? Stopwatch.GetTimestamp() : 0;
        ref DrDraw e = ref r.Draws[r.Cur & (DrawPool - 1)];
        if (e.TexCount >= MaxTexPerDraw || r.TexWritten - r.FrameTexStart >= MaxTexPerFrame)
        {
            r.FrameDroppedTex++;
            return;
        }
        ref DrTex t = ref r.Texs[r.TexWritten++ & (TexPool - 1)];
        t.Desc = desc;
        t.SrcAddr = _texSrcAddr;
        t.Unit = (byte)(unit | (vertex ? 0x80 : 0));
        t.Src = _texSrc | (failed ? TexSource.Failed : 0);
        e.TexCount++;
        if (r.Timing)
        {
            r.TimedTicks += Stopwatch.GetTimestamp() - t0;
        }
    }

    /// <summary>BeginScene, after the MSAA path was chosen.</summary>
    private void DrawRingScene(BeginSceneCommand beginSceneCommand)
    {
        DrawRing r = _drawRing!;
        if (r.ScenesWritten - r.FrameSceneStart >= MaxScenesPerFrame)
        {
            r.FrameDroppedScenes++;
            return;
        }
        ref DrScene s = ref r.Scenes[r.ScenesWritten++ & (ScenePool - 1)];
        RenderTarget? rt = _renderTarget;
        s.FirstDrawId = _drawSerial + 1;
        s.RtAddr = rt?.Address ?? 0;
        s.RtFormat = rt?.ColorFormat ?? 0;
        s.RtW = rt?.Width ?? 0;
        s.RtH = rt?.Height ?? 0;
        s.W = beginSceneCommand.Width;
        s.H = beginSceneCommand.Height;
        DepthSurfaceInfo? depthSurfaceInfo = beginSceneCommand.Depth;
        s.Ds = depthSurfaceInfo?.DepthData ?? 0;
        s.St = depthSurfaceInfo?.StencilData ?? 0;
        s.DsFormat = depthSurfaceInfo?.Format ?? 0;
        s.BgDepth = depthSurfaceInfo?.BackgroundDepth ?? 0;
        s.BgStencil = depthSurfaceInfo?.BackgroundStencil ?? 0;
        s.GxmMsaa = (byte)beginSceneCommand.Multisample;
        s.Msaa = (byte)(_multisampled ? (int)_msSamples : _supersampled ? 1 : 0);
        s.Flags = (byte)((beginSceneCommand.Color?.Downscale == true ? DrScDownscale : 0) | (depthSurfaceInfo?.ForceLoad == true ? DrScLoad : 0) | (depthSurfaceInfo?.ForceStore == true ? DrScStore : 0)
            | (depthSurfaceInfo != null ? DrScDepth : 0) | (beginSceneCommand.Color?.GammaMode is > 0 ? DrScGamma : 0) | (beginSceneCommand.Color != null ? DrScColor : 0));
    }

    /// <summary>Every present, before the present counter moves on: closes the frame (or creates/frees the ring when
    /// debug mode changed). With debug mode off this is a flag read and a null check.</summary>
    private void DrawRingPresent(DisplayFrame frame)
    {
        if (!DebugMode.Enabled)
        {
            if (_drawRing != null)
            {
                _drawRing = null;
                Log.Info($"[dbg] draw ring released at present #{_presentCount}");
            }
            return;
        }
        DrawRing? r = _drawRing;
        if (r == null)
        {
            // recording starts with the next frame, so every recorded frame is complete
            _drawRing = r = new DrawRing();
            r.FrameDrawStart = r.FrameTexStart = r.FrameSceneStart = 0;
            Dbg($"draw ring on: last {DrawRingFrames} frames, up to {DrawPool} draws / {TexPool} texture bindings / {ScenePool} scenes " +
                $"({((long)DrawPool * sizeof(DrDraw) + (long)TexPool * sizeof(DrTex) + (long)ScenePool * sizeof(DrScene) + (1 << 19)) >> 20} MB host memory)");
            return;
        }
        ref DrFrame f = ref r.Frames[r.FramesWritten++ % DrawRingFrames];
        f = new DrFrame
        {
            Present = _presentCount + 1,
            GuestFrame = DebugMode.Frame,
            TotalDraws = _drawSerial,
            Fb = frame.Address,
            DrawStart = r.FrameDrawStart,
            Draws = (int)(r.DrawsWritten - r.FrameDrawStart),
            TexStart = r.FrameTexStart,
            Texs = (int)(r.TexWritten - r.FrameTexStart),
            SceneStart = r.FrameSceneStart,
            Scenes = (int)(r.ScenesWritten - r.FrameSceneStart),
            DroppedDraws = r.FrameDroppedDraws,
            DroppedTex = r.FrameDroppedTex,
            DroppedScenes = r.FrameDroppedScenes,
        };
        _drawRingFramesTotal++;
        _drawRingDrawsTotal += f.Draws;
        if (r.TimedDraws > 0)
        {
            _drawRingTicksEst += r.TimedTicks * (double)f.Draws / r.TimedDraws;
            r.TimedTicks = 0;
            r.TimedDraws = 0;
        }
        r.FrameDrawStart = r.DrawsWritten;
        r.FrameTexStart = r.TexWritten;
        r.FrameSceneStart = r.ScenesWritten;
        r.FrameDroppedDraws = r.FrameDroppedTex = r.FrameDroppedScenes = 0;
        r.Cur = -1;
    }

    // ================================================================ capture

    private sealed class DrFrameShot
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
    private Task<int>? SaveDrawRing(string dir, long trigger)
    {
        if (_drawRing is not { } r)
        {
            return null;
        }

        var shots = new List<DrFrameShot>(DrawRingFrames);
        int evicted = 0;
        Dictionary<long, (FrameMetrics, FrameMetrics.Hit)>? replay = null;
        if (_replay is { } ring)
        {
            replay = new Dictionary<long, (FrameMetrics, FrameMetrics.Hit)>(ReplaySlots);
            foreach (ReplayEntry replayEntry in ring.Entries)
            {
                if (replayEntry.Ready)
                {
                    replay[replayEntry.Present] = (replayEntry.Metrics, replayEntry.Hits);
                }
            }
        }
        for (long k = Math.Max(0, r.FramesWritten - DrawRingFrames); k < r.FramesWritten; k++)
        {
            DrFrame drFrame = r.Frames[k % DrawRingFrames];
            if (drFrame.DrawStart < r.DrawsWritten - DrawPool || drFrame.TexStart < r.TexWritten - TexPool || drFrame.SceneStart < r.ScenesWritten - ScenePool)
            {
                evicted++;
                continue;
            }
            var s = new DrFrameShot { Frame = drFrame, Draws = new DrDraw[drFrame.Draws], Texs = new DrTex[drFrame.Texs], Scenes = new DrScene[drFrame.Scenes] };
            for (int i = 0; i < drFrame.Draws; i++)
            {
                s.Draws[i] = r.Draws[(drFrame.DrawStart + i) & (DrawPool - 1)];
            }

            for (int i = 0; i < drFrame.Texs; i++)
            {
                s.Texs[i] = r.Texs[(drFrame.TexStart + i) & (TexPool - 1)];
            }

            for (int i = 0; i < drFrame.Scenes; i++)
            {
                s.Scenes[i] = r.Scenes[(drFrame.SceneStart + i) & (ScenePool - 1)];
            }

            if (replay != null && replay.TryGetValue(drFrame.Present, out (FrameMetrics, FrameMetrics.Hit) m))
            {
                s.HasReplay = true;
                (s.Metrics, s.Hits) = m;
            }
            shots.Add(s);
        }
        Log.Info($"[diag] draw ring: {shots.Count} frames (presents #{(shots.Count > 0 ? shots[0].Frame.Present : 0)}-#{(shots.Count > 0 ? shots[^1].Frame.Present : 0)}){(evicted > 0 ? $", {evicted} overwritten" : "")}, writing in the background");
        long armed = _presentCount;
        return Task.Run(() => WriteDrawRing(Path.Combine(dir, "drawring"), shots, trigger, armed, evicted));
    }

    // ---------------------------------------------------------------- per-frame summary used by the index and the diff

    private sealed class DrFrameInfo
    {
        public readonly Dictionary<(ulong Vs, ulong Fs), int> Pairs = new();
        public readonly HashSet<ulong> Vs = new(), Fs = new();
        public readonly List<(uint Addr, uint Fmt, uint W, uint H)> Targets = new();
        /// <summary>texture (address, format) -> how it was resolved (first binding in the frame) and the first draw id.</summary>
        public readonly Dictionary<(uint Addr, uint Fmt), (TexSource Src, uint SrcAddr, int Draw)> TexSrc = new();
    }

    private static DrFrameInfo Summarize(DrFrameShot drFrameShot)
    {
        var info = new DrFrameInfo();
        var seenRt = new HashSet<(uint, uint)>();
        foreach (DrScene drScene in drFrameShot.Scenes)
        {
            if ((drScene.Flags & DrScColor) != 0 && seenRt.Add((drScene.RtAddr, drScene.RtFormat)))
            {
                info.Targets.Add((drScene.RtAddr, drScene.RtFormat, drScene.RtW, drScene.RtH));
            }
        }

        long texBase = drFrameShot.Frame.TexStart;
        foreach (DrDraw drDraw in drFrameShot.Draws)
        {
            info.Pairs[(drDraw.Vs, drDraw.Fs)] = info.Pairs.GetValueOrDefault((drDraw.Vs, drDraw.Fs)) + 1;
            info.Vs.Add(drDraw.Vs);
            info.Fs.Add(drDraw.Fs);
            for (int i = 0; i < drDraw.TexCount; i++)
            {
                long ti = drDraw.TexStart - texBase + i;
                if (ti < 0 || ti >= drFrameShot.Texs.Length)
                {
                    break;
                }

                DrTex drTex = drFrameShot.Texs[ti];
                info.TexSrc.TryAdd((drTex.Desc.DataAddress, drTex.Desc.Format), (drTex.Src & ~TexSource.Failed, drTex.SrcAddr, drDraw.Id));
            }
        }
        return info;
    }

    /// <summary>Lines of the difference of <paramref name="cur"/> against <paramref name="prev"/> (empty = same programs,
    /// targets and texture sources); <paramref name="structural"/> = programs, targets or texture sources differ.</summary>
    private static List<string> Diff(DrFrameShot prevShot, DrFrameInfo prev, DrFrameShot curShot, DrFrameInfo cur, out bool structural)
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
        foreach (((uint Addr, uint Fmt) key, (TexSource Src, uint SrcAddr, int Draw) c) in cur.TexSrc)
        {
            if (prev.TexSrc.TryGetValue(key, out (TexSource Src, uint SrcAddr, int Draw) p) && (p.Src != c.Src || p.SrcAddr != c.SrcAddr))
            {
                switched.Add($"0x{key.Addr:X8}:{key.Fmt:X8} {SrcName(p.Src, p.SrcAddr)} -> {SrcName(c.Src, c.SrcAddr)} (first draw id={c.Draw})");
            }
        }

        int texNew = cur.TexSrc.Keys.Count(k => !prev.TexSrc.ContainsKey(k)), texGone = prev.TexSrc.Keys.Count(k => !cur.TexSrc.ContainsKey(k));
        structural = addedFs.Count + removedFs.Count + addedVs.Count + removedVs.Count + addedRt.Count + removedRt.Count + switched.Count > 0;

        lines.Add($"draws {curShot.Draws.Length} ({dd:+0;-0;=}), scenes {curShot.Scenes.Length} ({ds:+0;-0;=}), textures {cur.TexSrc.Count} (+{texNew} -{texGone}), framebuffer 0x{fbA:X8}{(fbA != fbB ? $" (was 0x{fbB:X8})" : "")}");
        void Programs(string what, List<ulong> list, DrFrameInfo inFrame, bool fs)
        {
            if (list.Count == 0)
            {
                return;
            }

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
        if (changed.Count > 0)
        {
            lines.Add("draw count by program: " + string.Join(", ", changed.Select(x => $"fs {x.Key.Fs:X16}/vs {x.Key.Vs:X16} {x.Delta:+0;-0}")));
        }

        if (addedRt.Count > 0)
        {
            lines.Add("targets added: " + string.Join(", ", addedRt.Select(t => $"0x{t.Addr:X8}:{t.Fmt:X8} {t.W}x{t.H}")));
        }

        if (removedRt.Count > 0)
        {
            lines.Add("targets removed: " + string.Join(", ", removedRt.Select(t => $"0x{t.Addr:X8}:{t.Fmt:X8} {t.W}x{t.H}")));
        }

        if (switched.Count > 0)
        {
            lines.Add($"textures switched source ({switched.Count}): " + string.Join("; ", switched.Take(16)) + (switched.Count > 16 ? $"; +{switched.Count - 16} more" : ""));
        }

        return lines;
    }

    private static string SrcName(TexSource src, uint addr)
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
        if (addr != 0)
        {
            kind += $"@0x{addr:X8}";
        }

        if ((src & TexSource.SceneStart) != 0)
        {
            kind += "+scenestart";
        }

        if ((src & TexSource.LodLowered) != 0)
        {
            kind += "+lodmin";
        }

        if ((src & TexSource.Failed) != 0)
        {
            kind += "+FAILED";
        }

        return kind;
    }

    private static string MsaaName(byte m) => m switch { 0 => "off", 1 => "ss2x", _ => $"{m}x" };

    private static int WriteDrawRing(string dir, List<DrFrameShot> shots, long trigger, long armed, int evicted)
    {
        CultureInfo invariant = CultureInfo.InvariantCulture;
        Directory.CreateDirectory(dir);
        var infos = shots.Select(Summarize).ToList();
        var diffs = new List<string>[shots.Count];
        bool[] structural = new bool[shots.Count];
        for (int i = 0; i < shots.Count; i++)
        {
            diffs[i] = i == 0 ? ["(first frame in the ring)"] : Diff(shots[i - 1], infos[i - 1], shots[i], infos[i], out structural[i]);
        }
        // transient: differs from both neighbours while they match each other (a one-frame glitch stands out), and
        // also from the frames two away, so double-buffered A/B/A/B framebuffers or alternating passes do not count
        bool[] transient = new bool[shots.Count];
        bool Differs(int a, int b)
        {
            if (a < 0 || b < 0 || a >= shots.Count || b >= shots.Count)
            {
                return true;
            }

            Diff(shots[a], infos[a], shots[b], infos[b], out bool d);
            return d;
        }
        for (int i = 1; i + 1 < shots.Count; i++)
        {
            if (!structural[i] || !structural[i + 1] || Differs(i - 1, i + 1))
            {
                continue;
            }

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
            DrFrameShot s = shots[i];
            DrFrame f = s.Frame;
            DrFrameInfo info = infos[i];
            string news = "-";
            if (i > 0)
            {
                DrFrameInfo prev = infos[i - 1];
                var nf = info.Fs.Where(h => !prev.Fs.Contains(h)).Select(h => $"fs:{h:X16}").Concat(info.Vs.Where(h => !prev.Vs.Contains(h)).Select(h => $"vs:{h:X16}")).ToList();
                if (nf.Count > 0)
                {
                    news = string.Join(",", nf.Take(8)) + (nf.Count > 8 ? $",+{nf.Count - 8}" : "");
                }
            }
            string rts = info.Targets.Count == 0 ? "-" : string.Join(",", info.Targets.Take(16).Select(t => $"0x{t.Addr:X8}")) + (info.Targets.Count > 16 ? $",+{info.Targets.Count - 16}" : "");
            string rep = s.HasReplay ? string.Create(invariant, $"mean={s.Metrics.Mean:F3} hits={HitString(s.Hits)}") : "-";
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
            DrFrame f = shots[i].Frame;
            diff.AppendLine($"p{f.Present} f{f.GuestFrame}{(i > 0 ? $" vs p{shots[i - 1].Frame.Present}" : "")}{(transient[i] ? "  TRANSIENT" : "")}{(f.Present == trigger ? "  <== trigger" : "")}{(i > 0 && !structural[i] ? "  (same programs, targets and texture sources)" : "")}");
            foreach (string l in diffs[i])
            {
                diff.AppendLine("  " + l);
            }
        }
        File.WriteAllText(Path.Combine(dir, "diff.txt"), diff.ToString());

        var sb = new StringBuilder();
        for (int i = 0; i < shots.Count; i++)
        {
            sb.Clear();
            DrFrameShot s = shots[i];
            DrFrame f = s.Frame;
            sb.AppendLine($"# VitaSharp draw ring: the frame between presents #{f.Present - 1} and #{f.Present} (guest frame {f.GuestFrame}); its image is replay/frame_{f.Present:D6}.png{(f.Present == trigger ? "  <== trigger" : "")}{(transient[i] ? "  TRANSIENT" : "")}");
            sb.AppendLine($"# {f.Draws} draws recorded of {f.TotalDraws} issued, {f.Scenes} scenes, {s.Texs.Length} texture bindings{(f.DroppedDraws + f.DroppedTex + f.DroppedScenes > 0 ? $"; dropped over the per-frame cap: {f.DroppedDraws} draws, {f.DroppedTex} textures, {f.DroppedScenes} scenes" : "")}");
            sb.AppendLine("# same format as draws.txt minus vertex data and uniforms (a[], st[], v0/v1, vu/fu, vuf/fuh); extra fields: vh/fh = hint keys (shaders/<hash>_<hint>),");
            sb.AppendLine("# scene = scene index in this file, tN/vtN ...:Z<swizzle>:K<source> (see diff.txt), rtsize; [scene] adds fmt, rtsize, st, dsfmt, store, bgStencil, gamma, path");
            sb.AppendLine($"# diff vs previous frame{(i > 0 ? $" (p{shots[i - 1].Frame.Present})" : "")}:");
            foreach (string l in diffs[i])
            {
                sb.AppendLine("#   " + l);
            }

            int sc = 0;
            long texBase = f.TexStart;
            foreach (DrDraw drDraw in s.Draws)
            {
                // the scenes that began before this draw
                while (sc < s.Scenes.Length && s.Scenes[sc].FirstDrawId <= drDraw.Id)
                {
                    AppendScene(sb, invariant, s.Scenes[sc++], f.Present - 1);
                }

                AppendDraw(sb, invariant, drDraw, s, texBase, f.Present - 1);
            }
            while (sc < s.Scenes.Length)
            {
                AppendScene(sb, invariant, s.Scenes[sc++], f.Present - 1);
            }

            File.WriteAllText(Path.Combine(dir, $"frame_{f.Present:D6}.txt"), sb.ToString());
        }
        Log.Info($"[diag] draw ring written: {shots.Count} frames into {dir}");
        return shots.Count;
    }

    private static void AppendScene(StringBuilder sb, CultureInfo invariant, in DrScene drScene, long present)
    {
        bool depth = (drScene.Flags & DrScDepth) != 0;
        sb.Append(invariant, $"[scene] begin present={present} rt=0x{drScene.RtAddr:X8} {drScene.W}x{drScene.H} msaa={drScene.GxmMsaa} downscale={((drScene.Flags & DrScColor) != 0 ? ((drScene.Flags & DrScDownscale) != 0).ToString() : "")} ds=0x{drScene.Ds:X8}");
        sb.Append(invariant, $" load={(depth ? ((drScene.Flags & DrScLoad) != 0).ToString() : "")} bgDepth={(depth ? drScene.BgDepth.ToString(invariant) : "")} (next draw id={drScene.FirstDrawId})");
        sb.Append(invariant, $" fmt=0x{drScene.RtFormat:X8} rtsize={drScene.RtW}x{drScene.RtH} st=0x{drScene.St:X8} dsfmt=0x{drScene.DsFormat:X8} store={(drScene.Flags & DrScStore) != 0} bgStencil={drScene.BgStencil} gamma={(drScene.Flags & DrScGamma) != 0} path={MsaaName(drScene.Msaa)}\n");
    }

    private static void AppendDraw(StringBuilder sb, CultureInfo invariant, in DrDraw drDraw, DrFrameShot drFrameShot, long texBase, long present)
    {
        DrawState drawState = drDraw.State;
        sb.Append(invariant, $"[draw] id={drDraw.Id} f={present} rt=0x{drDraw.RtAddr:X8}:{drDraw.RtFormat:X8} ds=0x{drDraw.DsAddr:X8}/{drDraw.DsW}x{drDraw.DsH} vs={drDraw.Vs:X16} fs={drDraw.Fs:X16} prim={drDraw.Prim} n={drDraw.IndexCount}x{drDraw.InstanceCount}");
        GxmBlendInfo gxmBlendInfo = drDraw.Blend;
        sb.Append(invariant, $" blend={((drDraw.Flags & DrHasBlend) != 0 ? $"{gxmBlendInfo.ColorMask:X}:{gxmBlendInfo.ColorFunc}/{gxmBlendInfo.ColorSrc}/{gxmBlendInfo.ColorDst}" : "none")} fragEn={drawState.Front.FragmentEnabled} cull={drawState.CullMode} twoSided={drawState.TwoSided} depth={drawState.Front.DepthFunc}/{drawState.Front.DepthWrite} bias={drawState.Front.DepthBiasFactor}/{drawState.Front.DepthBiasUnits} st={drawState.Front.Stencil.Func}/{drawState.Back.Stencil.Func}");
        sb.Append(invariant, $" vp={drawState.ViewportEnabled}({drawState.VpOffX},{drawState.VpOffY},{drawState.VpScaleX},{drawState.VpScaleY},{drawState.VpOffZ},{drawState.VpScaleZ}) clip={drawState.RegionClipMode}[{drawState.ClipMinX},{drawState.ClipMinY}-{drawState.ClipMaxX},{drawState.ClipMaxY}]");
        for (int i = 0; i < drDraw.TexCount; i++)
        {
            long ti = drDraw.TexStart - texBase + i;
            if (ti < 0 || ti >= drFrameShot.Texs.Length)
            {
                break;
            }

            DrTex drTex = drFrameShot.Texs[ti];
            GxmTextureDesc x = drTex.Desc;
            sb.Append(invariant, $" {((drTex.Unit & 0x80) != 0 ? "vt" : "t")}{drTex.Unit & 0x7F}=0x{x.DataAddress:X8}:{x.Format:X8}:{x.Width}x{x.Height}:T{x.TextureType >> 29}:S{x.StrideBytes}:G{x.GammaMode}:F{x.MagFilter}{x.MinFilter}{x.MipFilter}:B{x.LodBias}:L{x.LodMin}:M{x.MipCount}:A{x.UAddr}{x.VAddr}:W0={x.W0:X8}:Z{x.Swizzle}:K{SrcName(drTex.Src, drTex.SrcAddr)}");
        }
        sb.Append(invariant, $" vh={drDraw.VsHint:X16} fh={drDraw.FsHint:X16} scene={(drDraw.Scene == 0xFFFF ? "-" : drDraw.Scene.ToString(invariant))} rtsize={drDraw.RtW}x{drDraw.RtH}");
        if ((drDraw.Flags & (DrVsFailed | DrFsFailed)) != 0)
        {
            sb.Append($" FAILED={((drDraw.Flags & DrVsFailed) != 0 ? "vs" : "")}{((drDraw.Flags & DrFsFailed) != 0 ? "fs" : "")}");
        }

        sb.Append(invariant, $" msaa={MsaaName(drDraw.Msaa)}\n");
    }
}
