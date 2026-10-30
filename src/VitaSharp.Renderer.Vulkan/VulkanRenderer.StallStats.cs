using VitaSharp.Core.Gpu;
using VitaSharp.Core.Util;

namespace VitaSharp.Renderer.Vulkan;

/// <summary>
/// Which draws stall the GPU thread on shader/pipeline work, by what they write (PERF stalls line at title exit).
/// Evidence for "skip the draw while its pipeline compiles": only a draw whose effects end in the presented image is a
/// candidate (no visibility test, no stencil or tile-word write, colour into a surface that is presented); the line
/// also reports how many hitch frames would remain if exactly those stalls disappeared. One record per stalled draw
/// (first uses only, so a few hundred per session).
/// </summary>
public sealed unsafe partial class VulkanRenderer
{
    [Flags]
    enum StallClass : ushort
    {
        None = 0,
        Visibility = 1,
        StencilWrite = 2,
        DepthWrite = 4,
        Color = 8,
        TileWord1Write = 16,
        FramebufferFetch = 32,
        Multisampled = 64,
        WritebackSized = 128,
    }

    readonly record struct StallRecord(long Present, uint Target, StallClass Class, long Ticks);

    readonly List<StallRecord> _stalls = new();
    readonly HashSet<uint> _presentedAddresses = new();

    void NoteDrawStall(DrawCommand d, byte colorMask, byte tw1Key, bool fetch, long ticks)
    {
        var st = d.State;
        var c = StallClass.None;
        if (d.VisAddress != 0) c |= StallClass.Visibility;
        if (_depth != null && WritesStencil(st)) c |= StallClass.StencilWrite;
        if (_depth != null && st.Front.DepthWrite) c |= StallClass.DepthWrite;
        if (_rt != null && colorMask != 0) c |= StallClass.Color;
        if (tw1Key == 2) c |= StallClass.TileWord1Write;
        if (fetch) c |= StallClass.FramebufferFetch;
        if (_ms) c |= StallClass.Multisampled;
        if (_rt != null && (ulong)_rt.Width * _rt.Height <= WritebackMaxPixels) c |= StallClass.WritebackSized;
        if (_stalls.Count < 100_000) _stalls.Add(new StallRecord(_presentCount, _rt?.Address ?? 0, c, ticks));
    }

    static bool WritesStencil(in DrawState st)
    {
        static bool Writes(in StencilState s) => s.WriteMask != 0 && (s.StencilFail != 0 || s.DepthFail != 0 || s.DepthPass != 0);
        return !(IsTrivial(st.Front.Stencil) && IsTrivial(st.Back.Stencil)) && (Writes(st.Front.Stencil) || (st.TwoSided && Writes(st.Back.Stencil)));
    }

    /// <summary>A draw that could be skipped without side effects beyond the presented image.</summary>
    bool SkipCandidate(in StallRecord r) =>
        (r.Class & (StallClass.Visibility | StallClass.StencilWrite | StallClass.TileWord1Write)) == 0
        && (r.Class & StallClass.Color) != 0 && _presentedAddresses.Contains(r.Target);

    string StallReport()
    {
        if (_stalls.Count == 0) return "PERF stalls: no draw stalled on shader/pipeline work";
        long total = 0, cand = 0, vis = 0, stencil = 0, tw1 = 0, offscreen = 0, offscreenSmall = 0, noColor = 0, ms = 0, depth = 0, fetch = 0;
        int nCand = 0, nVis = 0, nStencil = 0, nTw1 = 0, nOff = 0, nOffSmall = 0, nNoColor = 0;
        var perFrame = new Dictionary<long, (long All, long Safe)>();
        foreach (var r in _stalls)
        {
            total += r.Ticks;
            bool safe = SkipCandidate(r);
            if (safe)
            {
                cand += r.Ticks; nCand++;
                if ((r.Class & StallClass.Multisampled) != 0) ms += r.Ticks;
                if ((r.Class & StallClass.DepthWrite) != 0) depth += r.Ticks;
                if ((r.Class & StallClass.FramebufferFetch) != 0) fetch += r.Ticks;
            }
            if ((r.Class & StallClass.Visibility) != 0) { vis += r.Ticks; nVis++; }
            if ((r.Class & StallClass.StencilWrite) != 0) { stencil += r.Ticks; nStencil++; }
            if ((r.Class & StallClass.TileWord1Write) != 0) { tw1 += r.Ticks; nTw1++; }
            if ((r.Class & StallClass.Color) == 0) { noColor += r.Ticks; nNoColor++; }
            else if (!_presentedAddresses.Contains(r.Target))
            {
                offscreen += r.Ticks; nOff++;
                if ((r.Class & StallClass.WritebackSized) != 0) { offscreenSmall += r.Ticks; nOffSmall++; }
            }
            var f = perFrame.GetValueOrDefault(r.Present);
            perFrame[r.Present] = (f.All + r.Ticks, f.Safe + (safe ? r.Ticks : 0));
        }
        int hitch = 0, longHitch = 0, hitchAfter = 0, longAfter = 0;
        foreach (var (all, safe) in perFrame.Values)
        {
            if (Ms(all) >= HitchMs) hitch++;
            if (Ms(all) >= LongHitchMs) longHitch++;
            if (Ms(all - safe) >= HitchMs) hitchAfter++;
            if (Ms(all - safe) >= LongHitchMs) longAfter++;
        }
        return $"PERF stalls: {_stalls.Count} draws stalled {Ms(total):F0} ms on shader/pipeline work in {perFrame.Count} frames;"
            + $" skip candidates (colour into a presented surface, no visibility/stencil/tile-word write) {nCand} draws {Ms(cand):F0} ms"
            + $" (of which MSAA {Ms(ms):F0} ms, also writing depth {Ms(depth):F0} ms, programmable blending {Ms(fetch):F0} ms);"
            + $" must run: visibility {nVis} ({Ms(vis):F0} ms), stencil write {nStencil} ({Ms(stencil):F0} ms), tile word {nTw1} ({Ms(tw1):F0} ms),"
            + $" no colour (depth/stencil only) {nNoColor} ({Ms(noColor):F0} ms), off-screen targets {nOff} ({Ms(offscreen):F0} ms, {nOffSmall} into write-back-sized targets {Ms(offscreenSmall):F0} ms);"
            + $" stalled frames >= {HitchMs} ms {hitch} -> {hitchAfter} if the candidates were skipped, >= {LongHitchMs} ms {longHitch} -> {longAfter}";
    }
}
