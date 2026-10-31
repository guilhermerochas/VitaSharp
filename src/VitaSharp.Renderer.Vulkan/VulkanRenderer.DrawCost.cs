using Silk.NET.Vulkan;
using VitaSharp.Core.Gpu;
using VitaSharp.Core.Gxm.Shader;
using VitaSharp.Core.Util;

namespace VitaSharp.Renderer.Vulkan;

/// <summary>One executed draw for the mobile GPU cost estimate (replay-gpu --mali-cost).</summary>
public struct DrawCostRecord
{
    /// <summary>Present number the draw belongs to (the frame_N.png number) and its draw number in that frame.</summary>
    public long Present;
    public int Serial;
    public ulong VsHash, VsHint, FsHash, FsHint;
    /// <summary>Vertex shader invocations, upper bound: index count x instances (no post-transform cache).</summary>
    public long Vertices;
    /// <summary>Samples that passed the depth/stencil test (precise occlusion query; MSAA counts samples); -1 = not measured.</summary>
    public long Samples;
    /// <summary>Samples per pixel of the rendering (1 single-sampled).</summary>
    public int SampleCount;
    public uint RtW, RtH;
}

/// <summary>
/// Tooling for replay-gpu --mali-cost: every draw is wrapped in a precise occlusion query of its own (draws with a GXM
/// visibility test reuse theirs), and the draw, its shaders' GLSL and the frame's tile load/store bytes are handed to
/// <see cref="VulkanRendererOptions.DrawCostSink"/> once the frame's GPU work completed. Off (null sink) the renderer
/// records nothing extra; with it on, the commands and the images are the same apart from the queries.
/// </summary>
public sealed unsafe partial class VulkanRenderer
{
    const uint CostQueriesPerFrame = 16384;

    sealed class CostFrame
    {
        public QueryPool Pool;
        public uint Used;
        /// <summary>Draws of this submission; query index per draw (-1 none, else index, or ~visQuery for a visibility query).</summary>
        public readonly List<DrawCostRecord> Draws = new();
        public readonly List<(int First, int Count, int Vis)> Queries = new();
        public ulong[] Results = [];
    }

    Action<IReadOnlyList<DrawCostRecord>>? _costSink => _opt.DrawCostSink;
    readonly Dictionary<(ulong Hash, ulong Hint, bool Vertex), string> _costGlsl = new();
    readonly Dictionary<long, (long LoadBytes, long StoreBytes)> _costTileBytes = new();
    int _costDrawFirst = -1, _costDrawCount, _costVis = -1;
    uint _costOpen = uint.MaxValue;

    bool CostOn => _costSink != null;

    /// <summary>GLSL of every shader the cost probe saw, by (program hash, hint key, vertex).</summary>
    public IReadOnlyDictionary<(ulong Hash, ulong Hint, bool Vertex), string> DrawCostShaders => _costGlsl;

    /// <summary>Tile load/store bytes per present (scene renderings; colour 4 B and depth/stencil 4 B per pixel).</summary>
    public IReadOnlyDictionary<long, (long LoadBytes, long StoreBytes)> DrawCostTileBytes => _costTileBytes;

    CostFrame CostOf(Frame f)
    {
        if (f.Cost != null) return f.Cost;
        var ci = new QueryPoolCreateInfo { SType = StructureType.QueryPoolCreateInfo, QueryType = QueryType.Occlusion, QueryCount = CostQueriesPerFrame };
        var c = new CostFrame();
        _vk.CreateQueryPool(_dev, in ci, null, out c.Pool);
        f.Cost = c;
        return c;
    }

    void ResetCost(Frame f)
    {
        if (!CostOn) return;
        var c = CostOf(f);
        _vk.CmdResetQueryPool(f.Cmd, c.Pool, 0, CostQueriesPerFrame);
        c.Used = 0;
        c.Draws.Clear();
        c.Queries.Clear();
    }

    /// <summary>Before the draw commands of one piece of a draw (inside its rendering).</summary>
    void BeginCostQuery(CommandBuffer cmd, DrawCommand d)
    {
        if (!CostOn || d.VisAddress != 0) return; // a visibility query is begun instead (one occlusion query at a time)
        var c = CostOf(F);
        if (c.Used >= CostQueriesPerFrame) { Log.WarnOnce("costq-full", "[cost] occlusion queries per frame exhausted"); return; }
        _costOpen = c.Used++;
        if (_costDrawFirst < 0) _costDrawFirst = (int)_costOpen;
        _costDrawCount++;
        _vk.CmdBeginQuery(cmd, c.Pool, _costOpen, _core.OcclusionQueryPrecise ? QueryControlFlags.PreciseBit : 0);
    }

    void EndCostQuery(CommandBuffer cmd)
    {
        if (_costOpen == uint.MaxValue) return;
        _vk.CmdEndQuery(cmd, CostOf(F).Pool, _costOpen);
        _costOpen = uint.MaxValue;
    }

    void CostNoteVisQuery(uint query) { if (CostOn) _costVis = (int)query; }

    /// <summary>After the draw was recorded: its record (the shaders are the variants it was drawn with).</summary>
    void CostNoteDraw(DrawCommand d, VsEntry vs, FsEntry fs)
    {
        var c = CostOf(F);
        c.Draws.Add(new DrawCostRecord
        {
            Present = _presentCount + 1, Serial = _drawSerial,
            VsHash = vs.ProgramHash, VsHint = vs.HintKey, FsHash = fs.ProgramHash, FsHint = fs.HintKey,
            Vertices = (long)d.IndexCount * Math.Max(1u, d.InstanceCount),
            Samples = -1, SampleCount = (int)SceneSamples, RtW = _rt?.Width ?? _depth?.Width ?? 0, RtH = _rt?.Height ?? _depth?.Height ?? 0,
        });
        c.Queries.Add((_costDrawFirst, _costDrawCount, _costVis));
        _costDrawFirst = -1; _costDrawCount = 0; _costVis = -1;
        if (!_costGlsl.ContainsKey((vs.ProgramHash, vs.HintKey, true)))
            _costGlsl[(vs.ProgramHash, vs.HintKey, true)] = !string.IsNullOrEmpty(vs.Gen?.Glsl) ? vs.Gen!.Glsl : TryGenerate(d.VertexProgram.Info.Program, () => VertexHints(d, 0));
        if (!_costGlsl.ContainsKey((fs.ProgramHash, fs.HintKey, false)))
            _costGlsl[(fs.ProgramHash, fs.HintKey, false)] = !string.IsNullOrEmpty(fs.Gen?.Glsl) ? fs.Gen!.Glsl : TryGenerate(d.FragmentProgram.Info.Program, () => FragmentHints(d, _rt?.ColorFormat ?? 0));
    }

    static string TryGenerate(GxpProgram program, Func<ShaderHints> hints)
    {
        try { return ShaderGenerator.Generate(program, hints()).Glsl; }
        catch (Exception e) { Log.WarnOnce($"cost-gen-{program.Hash:x16}", $"[cost] GLSL of {program.Hash:x16} not regenerated: {e.Message}"); return ""; }
    }

    /// <summary>Tile bytes of one scene rendering (called with the load/store counters).</summary>
    void CostNoteOps(bool color, AttachmentLoadOp colorLoad, AttachmentStoreOp colorStore, bool depth, AttachmentLoadOp depthLoad, AttachmentStoreOp depthStore, Extent2D area)
    {
        if (!CostOn) return;
        long px = area.Width != 0 ? (long)area.Width * area.Height : (long)(_rt?.Width ?? _depth?.Width ?? 0) * (_rt?.Height ?? _depth?.Height ?? 0);
        long load = 0, store = 0;
        if (color) { if (colorLoad == AttachmentLoadOp.Load) load += px * 4; if (colorStore == AttachmentStoreOp.Store) store += px * 4; }
        if (depth) { if (depthLoad == AttachmentLoadOp.Load) load += px * 4; if (depthStore == AttachmentStoreOp.Store) store += px * 4; }
        var cur = _costTileBytes.GetValueOrDefault(_presentCount + 1);
        _costTileBytes[_presentCount + 1] = (cur.LoadBytes + load, cur.StoreBytes + store);
    }

    /// <summary>After the frame's fence (and its visibility results): sample counts into the records, then the sink.</summary>
    void CompleteCost(Frame f)
    {
        var c = f.Cost;
        if (c == null || _costSink == null || c.Draws.Count == 0) return;
        if (c.Results.Length < c.Used) c.Results = new ulong[CostQueriesPerFrame];
        if (c.Used > 0)
            fixed (ulong* p = c.Results)
                _vk.GetQueryPoolResults(_dev, c.Pool, 0, c.Used, (nuint)(c.Used * 8), p, 8, QueryResultFlags.Result64Bit | QueryResultFlags.ResultWaitBit);
        var vis = f.Visibility?.Results;
        var span = System.Runtime.InteropServices.CollectionsMarshal.AsSpan(c.Draws);
        for (int i = 0; i < span.Length; i++)
        {
            var (first, count, v) = c.Queries[i];
            if (v >= 0) span[i].Samples = vis != null && v < vis.Length ? vis[v] : -1;
            else if (count > 0)
            {
                ulong s = 0;
                for (int q = first; q < first + count; q++) s += c.Results[q];
                span[i].Samples = (long)s;
            }
        }
        _costSink(c.Draws);
        c.Draws.Clear();
        c.Queries.Clear();
        c.Used = 0;
    }

    void DestroyCost(Frame f)
    {
        if (f.Cost is { } c) _vk.DestroyQueryPool(_dev, c.Pool, null);
        f.Cost = null;
    }
}
