using System.Runtime.CompilerServices;
using Silk.NET.Vulkan;
using VitaSharp.Core.Util;

namespace VitaSharp.Renderer.Vulkan;

/// <summary>
/// Fewer scene breaks. A Vita scene is one pass over the tiles; the renderer used to end its Vulkan rendering for every
/// texture upload and copy recorded in the middle of it (Killzone: ~13 renderings per scene), and on tile-based GPUs
/// each break costs a store of the tiles and a load of them again.
/// <para>
/// Scene renderings are recorded into secondary command buffers and begun in the primary only when they end (as on
/// tile-based GPUs and multisampled scenes before; now on every GPU unless VITASHARP_SCENE_BREAKS=strict). Until then
/// the primary has not begun the rendering, so work that has to run outside a rendering (an upload, a copy, a layout
/// barrier) can be recorded into the primary directly: it lands before the rendering's begin and the rendering goes
/// on. That is "hoisting", and it is exact when the work
/// <list type="bullet">
/// <item>does not read what the open rendering writes (the scene's colour surface, its samples, depth/stencil or tile
/// word): those reads keep the strict order and end the rendering (<see cref="BeginOutside"/> with <c>reads</c>);</item>
/// <item>does not write an image that a draw already recorded in the open rendering samples (the draw would see the
/// new content). Every sampled view is stamped with the rendering it is bound in (<see cref="NoteSampled"/>); a
/// write into a stamped view ends the rendering.</item>
/// </list>
/// Everything recorded before the hoisted work in the primary (earlier renderings, earlier hoisted work) stays before
/// it, so the order of all other work is unchanged.
/// </para>
/// </summary>
public sealed unsafe partial class VulkanRenderer
{
    /// <summary>VITASHARP_SCENE_BREAKS=strict: no hoisting, and desktop GPUs begin single-sampled renderings directly.</summary>
    static readonly bool StrictSceneBreaks = string.Equals(Environment.GetEnvironmentVariable(EnvVars.SceneBreaks)?.Trim(), "strict", StringComparison.OrdinalIgnoreCase);

    /// <summary>Single-sampled renderings are recorded into secondaries and begun when they end.</summary>
    bool DeferredRenderings => TiledRenderings || UseRenderPasses || !StrictSceneBreaks;

    /// <summary>Work is being recorded into the primary, before the open (deferred) rendering.</summary>
    bool _hoisting;
    /// <summary>Serial of the open rendering (bumped when one begins).</summary>
    long _renderingSerial;
    /// <summary>Image view -> serial of the latest rendering a draw binds it in (an upper bound: the next serial
    /// when no rendering is open, see <see cref="NoteSampled"/>).</summary>
    readonly Dictionary<ulong, long> _sampledIn = new();

    long _statRenderings, _statScenes, _statHoisted;
    readonly Dictionary<string, long> _breakReasons = new(), _hoistReasons = new();

    /// <summary>A draw is about to bind <paramref name="view"/> for sampling. The draw lands in the open rendering or,
    /// if one ends before the draw is recorded, in the next one: the stamp is the later of the two.</summary>
    void NoteSampled(ImageView view)
    {
        if (view.Handle != 0) _sampledIn[view.Handle] = _renderingSerial + 1;
    }

    /// <summary>A draw of the open rendering, or of an ended one that will be begun together with it (merge, see
    /// VulkanRenderer.Tiler.cs), samples <paramref name="view"/>.</summary>
    bool SampledByOpenRendering(ImageView view) =>
        view.Handle != 0 && _sampledIn.TryGetValue(view.Handle, out long s) && s >= (_chain.Count > 0 ? _chainStartSerial : _renderingSerial);

    /// <summary>Pixel rectangles (inclusive) the draws of the open rendering, and of the ended ones waiting to be
    /// begun with it, may have written (their region clips): a copy of another part of the scene's own surface sees
    /// the same pixels before them as after them (post-processing atlases: Killzone draws pass after pass into regions
    /// of one surface, each reading the previous region).</summary>
    readonly List<(int X0, int Y0, int X1, int Y1)> _drawnRects = new();
    bool _drawnAll;

    void ResetDrawn()
    {
        _drawnRects.Clear();
        _drawnAll = false;
    }

    /// <summary>A draw into the scene's surface was recorded in the open rendering.</summary>
    void NoteDrawn()
    {
        if (_drawnAll) return;
        if (_drawMinX <= 0 && _drawMinY <= 0 && _drawMaxX == int.MaxValue && _drawMaxY == int.MaxValue) { _drawnAll = true; return; }
        var r = (_drawMinX, _drawMinY, _drawMaxX, _drawMaxY);
        if (_drawnRects.Count > 0 && _drawnRects[^1] == r) return;
        if (_drawnRects.Count >= 64) { _drawnAll = true; return; }
        _drawnRects.Add(r);
    }

    /// <summary>The surface rectangle may hold pixels drawn by the open rendering (or the renderings merged with it).</summary>
    bool DrawnInOpen(uint x, uint y, uint w, uint h)
    {
        if (_drawnAll) return true;
        long x1 = (long)x + w - 1, y1 = (long)y + h - 1;
        foreach (var r in _drawnRects)
            if (r.X0 <= x1 && r.X1 >= x && r.Y0 <= y1 && r.Y1 >= y) return true;
        return false;
    }

    /// <summary>Counted for the PERF gpu line: why renderings were ended before their scene did.</summary>
    void NoteBreak(string caller)
    {
        if (!_rendering) return;
        _breakReasons[caller] = _breakReasons.GetValueOrDefault(caller) + 1;
    }

    /// <summary>
    /// Work outside the rendering that writes <paramref name="writes"/> (a sampled view; default = none) and reads
    /// <paramref name="reads"/> (a colour surface; null = none, only guest memory). Hoisted before the open rendering
    /// when that is exact (see the class summary), else the rendering ends as <see cref="PauseRendering"/> does.
    /// Dispose the result once the work is recorded.
    /// </summary>
    /// <param name="readRect">the part of <paramref name="reads"/> that is read: when it is the scene's own surface and
    /// no draw of the open rendering touched that part, the work is still hoisted (plain single-sampled scenes only)</param>
    OutsideScope BeginOutside(ImageView writes = default, RenderTarget? reads = null, bool readsDepth = false, (uint X, uint Y, uint W, uint H)? readRect = null,
        [CallerMemberName] string caller = "")
    {
        if (_hoisting) return default; // nested: already in the primary
        bool readsOwn = reads != null && reads == _rt
            && !(readRect is { } rr && !_ms && !_ss && !_msaaNoDownscale && _tileCmd.Handle != 0 && !DrawnInOpen(rr.X, rr.Y, rr.W, rr.H));
        if (_rendering && !StrictSceneBreaks && (_tileCmd.Handle != 0 || _msCmd.Handle != 0)
            && !readsOwn && !readsDepth && !SampledByOpenRendering(writes))
        {
            _hoisting = true;
            _statHoisted++;
            _hoistReasons[caller] = _hoistReasons.GetValueOrDefault(caller) + 1;
            return new OutsideScope(this);
        }
        PauseRendering(reads, caller);
        return default;
    }

    readonly struct OutsideScope(VulkanRenderer? owner) : IDisposable
    {
        public void Dispose() { if (owner != null) owner._hoisting = false; }
    }

    /// <summary>Commands are recorded into the primary between renderings: ended renderings waiting for a merge are
    /// begun first.</summary>
    CommandBuffer PrimaryTouched()
    {
        _primaryTouches++;
        // ended renderings waiting to be merged with the next one are begun before anything else goes in
        if (_chain.Count > 0) FlushChain(false);
        return F.Cmd;
    }

    long _primaryTouches;

    static string TopReasons(Dictionary<string, long> d) =>
        d.Count == 0 ? "none" : string.Join(", ", d.OrderByDescending(kv => kv.Value).Take(6).Select(kv => $"{kv.Key} {kv.Value}"));

    /// <summary>"renderings: N in M scenes (R per scene), hoisted H (...), breaks (...)" for the PERF gpu line.</summary>
    string SceneBreakReport() =>
        $"renderings {_statRenderings - _statMerged} in {_statScenes} scenes ({(double)(_statRenderings - _statMerged) / Math.Max(1, _statScenes):F2} per scene{(StrictSceneBreaks ? ", strict" : "")}; " +
        $"{_statMerged} more merged into the previous one), hoisted {_statHoisted} ({TopReasons(_hoistReasons)}), mid-scene breaks: {TopReasons(_breakReasons)}";
}
