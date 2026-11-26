using Silk.NET.Vulkan;
using VitaSharp.Core.Gpu;
using VitaSharp.Core.Gxm.Shader;
using VitaSharp.Core.Memory;
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
    /// <summary>The GXM scene the draw belongs to (renderer scene serial) and its colour surface address.</summary>
    public long Scene;
    public uint RtAddress;
    /// <summary>GXM primitive type (0 triangles, 1 lines, 2 points, 3 strip, 4 fan, 5 triangle edges) and index size.</summary>
    public byte PrimitiveType;
    public bool Index32;
    /// <summary>Distinct indices of the draw, the index range (max - min + 1) and the vertex-shader invocations a
    /// 32-entry FIFO post-transform cache would leave (<see cref="IndexStats"/>).</summary>
    public long UniqueVertices, IndexRange, CacheMisses32;

    /// <summary>
    /// Index statistics of one draw: distinct indices, the index range, and the misses of a FIFO post-transform cache
    /// of <paramref name="cacheSize"/> entries (each miss is one vertex shader invocation on a GPU that shades
    /// through such a cache). Pure, unit-tested (MaliPassCostTests).
    /// </summary>
    public static (long Unique, long Range, long Misses) IndexStats(ReadOnlySpan<byte> raw, bool index32, int cacheSize = 32)
    {
        int count = raw.Length / (index32 ? 4 : 2);
        if (count == 0)
        {
            return (0, 0, 0);
        }

        var seen = new HashSet<uint>();
        Span<uint> fifo = stackalloc uint[Math.Clamp(cacheSize, 1, 64)];
        int fifoLen = 0, fifoNext = 0;
        long misses = 0;
        uint min = uint.MaxValue, max = 0;
        for (int i = 0; i < count; i++)
        {
            uint v = index32 ? System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(raw[(i * 4)..]) : System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(raw[(i * 2)..]);
            seen.Add(v);
            min = Math.Min(min, v);
            max = Math.Max(max, v);
            bool hit = false;
            for (int k = 0; k < fifoLen; k++)
            {
                if (fifo[k] == v)
                {
                    hit = true;
                    break;
                }
            }

            if (!hit)
            {
                misses++;
                fifo[fifoNext] = v;
                fifoNext = (fifoNext + 1) % fifo.Length;
                fifoLen = Math.Min(fifoLen + 1, fifo.Length);
            }
        }

        return (seen.Count, (long)max - min + 1, misses);
    }
}

/// <summary>One scene rendering (render pass) for the mobile GPU cost estimate: its tile load/store/resolve bytes.</summary>
public struct PassCostRecord
{
    public long Present, Scene;
    /// <summary>Recording order among the passes and copies.</summary>
    public long Order;
    /// <summary>Where it was begun (the renderer method: FlushChain, BeginSingleFetchPass, EndMsaaRendering...).</summary>
    public string Site;
    public uint Width, Height, RtAddress;
    public Format ColorFormat;
    public int Samples;
    public bool HasColor, ColorLoad, ColorStore, DepthLoad, DepthStore, Resolve;
    public long LoadBytes, StoreBytes, ResolveBytes;

    /// <summary>
    /// Tile traffic of a rendering of <paramref name="width"/> x <paramref name="height"/> pixels: colour at its format's
    /// size and depth/stencil at 4 bytes (D24S8), each times the samples per pixel when loaded or stored; a resolve writes
    /// one colour value per pixel; <paramref name="colorSamples"/> (0 = <paramref name="samples"/>) is the colour
    /// attachment's own count (render to single-sampled: 1, its samples never leave the tile memory). Pure, unit-tested
    /// (MaliPassCostTests).
    /// </summary>
    public static (long Load, long Store, long Resolve) Bytes(long width, long height, double colorBpp, int samples,
        bool hasColor, bool colorLoad, bool colorStore, bool depthLoad, bool depthStore, bool resolve, int colorSamples = 0)
    {
        long px = width * height;
        long sampled = px * Math.Max(1, samples);
        long colorBytes = hasColor ? (long)(px * Math.Max(1, colorSamples > 0 ? colorSamples : samples) * colorBpp) : 0;
        long depthBytes = sampled * 4;
        long load = (colorLoad ? colorBytes : 0) + (depthLoad ? depthBytes : 0);
        long store = (colorStore ? colorBytes : 0) + (depthStore ? depthBytes : 0);
        long res = hasColor && resolve ? (long)(px * colorBpp) : 0;
        return (load, store, res);
    }
}

/// <summary>One transfer outside the renderings (copy, blit, upload, readback) for the mobile GPU cost estimate.</summary>
public struct CopyCostRecord
{
    public long Present, Scene;
    /// <summary>Recording order among the passes and copies.</summary>
    public long Order;
    /// <summary>The renderer method that recorded it (MirrorRenderTarget, UploadTexture, ...).</summary>
    public string Site;
    /// <summary>Bytes read plus bytes written.</summary>
    public long Bytes;
}

/// <summary>
/// Tooling for replay-gpu --mali-cost: every draw is wrapped in a precise occlusion query of its own (draws with a GXM
/// visibility test reuse theirs), and the draw, its shaders' GLSL and the frame's tile load/store bytes are handed to
/// <see cref="VulkanRendererOptions.DrawCostSink"/> once the frame's GPU work completed. Off (null sink) the renderer
/// records nothing extra; with it on, the commands and the images are the same apart from the queries.
/// </summary>
public sealed unsafe partial class VulkanRenderer
{
    private const uint CostQueriesPerFrame = 16384;

    private sealed class CostFrame
    {
        public QueryPool Pool;
        public uint Used;
        /// <summary>Draws of this submission; query index per draw (-1 none, else index, or ~visQuery for a visibility query).</summary>
        public readonly List<DrawCostRecord> Draws = new();
        public readonly List<(int First, int Count, int Vis)> Queries = new();
        public ulong[] Results = [];
    }

    private Action<IReadOnlyList<DrawCostRecord>>? CostSink => _options.DrawCostSink;
    private readonly Dictionary<(ulong Hash, ulong Hint, bool Vertex), string> _costGlsl = new();
    private readonly Dictionary<long, (long LoadBytes, long StoreBytes)> _costTileBytes = new();
    private readonly List<PassCostRecord> _costPasses = new();
    private readonly List<CopyCostRecord> _costCopies = new();
    private long _costOrder;
    private int _costDrawFirst = -1, _costDrawCount, _costVis = -1;
    private uint _costOpen = uint.MaxValue;

    private bool CostOn => CostSink != null;

    /// <summary>GLSL of every shader the cost probe saw, by (program hash, hint key, vertex).</summary>
    public IReadOnlyDictionary<(ulong Hash, ulong Hint, bool Vertex), string> DrawCostShaders => _costGlsl;

    /// <summary>Tile load/store bytes per present (scene renderings; colour 4 B and depth/stencil 4 B per pixel).</summary>
    public IReadOnlyDictionary<long, (long LoadBytes, long StoreBytes)> DrawCostTileBytes => _costTileBytes;

    /// <summary>Every scene rendering recorded while the cost sink was set (recording order).</summary>
    public IReadOnlyList<PassCostRecord> DrawCostPasses => _costPasses;

    /// <summary>Every transfer recorded while the cost sink was set (recording order).</summary>
    public IReadOnlyList<CopyCostRecord> DrawCostCopies => _costCopies;

    /// <summary>Bytes per texel of a host format (block formats: the block bytes over its texels).</summary>
    internal static double CostBytesPerTexel(Format f) => f switch
    {
        Format.R8Unorm or Format.R8SNorm or Format.R8Uint or Format.S8Uint => 1,
        Format.R8G8Unorm or Format.R8G8SNorm or Format.R16Unorm or Format.R16SNorm or Format.R16Sfloat or Format.R16Uint
            or Format.R5G6B5UnormPack16 or Format.B5G6R5UnormPack16 or Format.A1R5G5B5UnormPack16 or Format.R5G5B5A1UnormPack16
            or Format.R4G4B4A4UnormPack16 or Format.B4G4R4A4UnormPack16 or Format.D16Unorm => 2,
        Format.R16G16B16A16Sfloat or Format.R16G16B16A16Unorm or Format.R32G32Sfloat or Format.R32G32Uint => 8,
        Format.R32G32B32A32Sfloat or Format.R32G32B32A32Uint => 16,
        Format.BC1RgbUnormBlock or Format.BC1RgbaUnormBlock or Format.BC1RgbSrgbBlock or Format.BC1RgbaSrgbBlock
            or Format.BC4UnormBlock or Format.BC4SNormBlock or Format.Etc2R8G8B8UnormBlock or Format.EacR11UnormBlock => 0.5,
        Format.BC2UnormBlock or Format.BC3UnormBlock or Format.BC5UnormBlock or Format.BC5SNormBlock or Format.BC2SrgbBlock
            or Format.BC3SrgbBlock or Format.Astc4x4UnormBlock or Format.Astc4x4SrgbBlock or Format.Etc2R8G8B8A8UnormBlock => 1,
        _ => 4,
    };

    /// <summary>A transfer of <paramref name="texels"/> texels of <paramref name="format"/>, read once and written once.</summary>
    private void CostCopy(long texels, Format format, [System.Runtime.CompilerServices.CallerMemberName] string site = "")
    {
        if (!CostOn)
        {
            return;
        }

        CostCopyBytes((long)(texels * CostBytesPerTexel(format) * 2), site);
    }

    private void CostCopy(in Extent3D extent, Format format, [System.Runtime.CompilerServices.CallerMemberName] string site = "") =>
        CostCopy((long)extent.Width * extent.Height * Math.Max(1u, extent.Depth), format, site);

    /// <summary>A buffer-to-buffer copy (or any transfer already in bytes read + written).</summary>
    private void CostCopyBytes(long bytes, [System.Runtime.CompilerServices.CallerMemberName] string site = "")
    {
        if (!CostOn)
        {
            return;
        }

        _costCopies.Add(new CopyCostRecord { Present = _presentCount + 1, Scene = _sceneSerial, Order = _costOrder++, Site = site, Bytes = bytes });
    }

    private CostFrame CostOf(Frame f)
    {
        if (f.Cost != null)
        {
            return f.Cost;
        }

        var ci = new QueryPoolCreateInfo { SType = StructureType.QueryPoolCreateInfo, QueryType = QueryType.Occlusion, QueryCount = CostQueriesPerFrame };
        var c = new CostFrame();
        _vk.CreateQueryPool(_dev, in ci, null, out c.Pool);
        f.Cost = c;
        return c;
    }

    private void ResetCost(Frame f)
    {
        if (!CostOn)
        {
            return;
        }

        CostFrame costFrame = CostOf(f);
        _vk.CmdResetQueryPool(f.Cmd, costFrame.Pool, 0, CostQueriesPerFrame);
        costFrame.Used = 0;
        costFrame.Draws.Clear();
        costFrame.Queries.Clear();
    }

    /// <summary>Before the draw commands of one piece of a draw (inside its rendering).</summary>
    private void BeginCostQuery(CommandBuffer cmd, DrawCommand d)
    {
        if (!CostOn || d.VisAddress != 0)
        {
            return; // a visibility query is begun instead (one occlusion query at a time)
        }

        CostFrame costFrame = CostOf(F);
        if (costFrame.Used >= CostQueriesPerFrame)
        {
            Log.WarnOnce("costq-full", "[cost] occlusion queries per frame exhausted");
            return;
        }
        _costOpen = costFrame.Used++;
        if (_costDrawFirst < 0)
        {
            _costDrawFirst = (int)_costOpen;
        }

        _costDrawCount++;
        _vk.CmdBeginQuery(cmd, costFrame.Pool, _costOpen, _core.OcclusionQueryPrecise ? QueryControlFlags.PreciseBit : 0);
    }

    private void EndCostQuery(CommandBuffer cmd)
    {
        if (_costOpen == uint.MaxValue)
        {
            return;
        }

        _vk.CmdEndQuery(cmd, CostOf(F).Pool, _costOpen);
        _costOpen = uint.MaxValue;
    }

    private void CostNoteVisQuery(uint query)
    {
        if (CostOn)
        {
            _costVis = (int)query;
        }
    }

    /// <summary>After the draw was recorded: its record (the shaders are the variants it was drawn with).</summary>
    private void CostNoteDraw(DrawCommand d, VsEntry vs, FsEntry fs)
    {
        CostFrame costFrame = CostOf(F);
        costFrame.Draws.Add(new DrawCostRecord
        {
            Present = _presentCount + 1,
            Serial = _drawSerial,
            VsHash = vs.ProgramHash,
            VsHint = vs.HintKey,
            FsHash = fs.ProgramHash,
            FsHint = fs.HintKey,
            Vertices = (long)d.IndexCount * Math.Max(1u, d.InstanceCount),
            Samples = -1,
            SampleCount = (int)SceneSamples,
            RtW = _renderTarget?.Width ?? _depth?.Width ?? 0,
            RtH = _renderTarget?.Height ?? _depth?.Height ?? 0,
            Scene = _sceneSerial,
            RtAddress = _renderTarget?.Address ?? 0,
            PrimitiveType = d.PrimitiveType,
            Index32 = d.Index32,
        });
        if (d.IndexAddress != 0 && d.IndexCount is > 0 and <= 1u << 20)
        {
            (long unique, long range, long misses) = DrawCostRecord.IndexStats(GuestMemory.ReadSpan(d.IndexAddress, (int)(d.IndexCount * (d.Index32 ? 4u : 2u))), d.Index32);
            Span<DrawCostRecord> added = System.Runtime.InteropServices.CollectionsMarshal.AsSpan(costFrame.Draws);
            added[^1].UniqueVertices = unique;
            added[^1].IndexRange = range;
            added[^1].CacheMisses32 = misses;
        }
        costFrame.Queries.Add((_costDrawFirst, _costDrawCount, _costVis));
        _costDrawFirst = -1;
        _costDrawCount = 0;
        _costVis = -1;
        if (!_costGlsl.ContainsKey((vs.ProgramHash, vs.HintKey, true)))
        {
            _costGlsl[(vs.ProgramHash, vs.HintKey, true)] = !string.IsNullOrEmpty(vs.Gen?.Glsl) ? vs.Gen!.Glsl : TryGenerate(d.VertexProgram.Info.Program, () => VertexHints(d, VertexVaryingMask(d)));
        }

        if (!_costGlsl.ContainsKey((fs.ProgramHash, fs.HintKey, false)))
        {
            _costGlsl[(fs.ProgramHash, fs.HintKey, false)] = !string.IsNullOrEmpty(fs.Gen?.Glsl) ? fs.Gen!.Glsl : TryGenerate(d.FragmentProgram.Info.Program, () => FragmentHints(d, _renderTarget?.ColorFormat ?? 0));
        }
    }

    private static string TryGenerate(GxpProgram program, Func<ShaderHints> hints)
    {
        try
        {
            return ShaderGenerator.Generate(program, hints()).Glsl;
        }
        catch (Exception e)
        {
            Log.WarnOnce($"cost-gen-{program.Hash:x16}", $"[cost] GLSL of {program.Hash:x16} not regenerated: {e.Message}");
            return "";
        }
    }

    /// <summary>Tile bytes of one scene rendering (called with the load/store counters).</summary>
    private void CostNoteOps(bool color, AttachmentLoadOp colorLoad, AttachmentStoreOp colorStore, bool depth, AttachmentLoadOp depthLoad, AttachmentStoreOp depthStore, Extent2D area,
        Format colorFormat, int samples, bool resolve, int colorSamples, string site)
    {
        if (!CostOn)
        {
            return;
        }

        uint width = area.Width != 0 ? area.Width : _renderTarget?.Width ?? _depth?.Width ?? 0;
        uint height = area.Width != 0 ? area.Height : _renderTarget?.Height ?? _depth?.Height ?? 0;
        if (colorFormat == Format.Undefined)
        {
            colorFormat = _renderTarget?.Format ?? Format.R8G8B8A8Unorm;
        }

        var pass = new PassCostRecord
        {
            Present = _presentCount + 1,
            Scene = _sceneSerial,
            Order = _costOrder++,
            Site = site,
            Width = width,
            Height = height,
            RtAddress = _renderTarget?.Address ?? 0,
            ColorFormat = colorFormat,
            Samples = Math.Max(1, samples),
            HasColor = color,
            ColorLoad = color && colorLoad == AttachmentLoadOp.Load,
            ColorStore = color && colorStore == AttachmentStoreOp.Store,
            DepthLoad = depth && depthLoad == AttachmentLoadOp.Load,
            DepthStore = depth && depthStore == AttachmentStoreOp.Store,
            Resolve = resolve,
        };
        (pass.LoadBytes, pass.StoreBytes, pass.ResolveBytes) = PassCostRecord.Bytes(width, height, CostBytesPerTexel(colorFormat), pass.Samples,
            color, pass.ColorLoad, pass.ColorStore, pass.DepthLoad, pass.DepthStore, resolve, colorSamples);
        _costPasses.Add(pass);
        (long LoadBytes, long StoreBytes) cur = _costTileBytes.GetValueOrDefault(_presentCount + 1);
        _costTileBytes[_presentCount + 1] = (cur.LoadBytes + pass.LoadBytes, cur.StoreBytes + pass.StoreBytes + pass.ResolveBytes);
    }

    /// <summary>After the frame's fence (and its visibility results): sample counts into the records, then the sink.</summary>
    private void CompleteCost(Frame f)
    {
        CostFrame? costFrame = f.Cost;
        if (costFrame == null || CostSink == null || costFrame.Draws.Count == 0)
        {
            return;
        }

        if (costFrame.Results.Length < costFrame.Used)
        {
            costFrame.Results = new ulong[CostQueriesPerFrame];
        }

        if (costFrame.Used > 0)
        {
            fixed (ulong* p = costFrame.Results)
            {
                _vk.GetQueryPoolResults(_dev, costFrame.Pool, 0, costFrame.Used, (nuint)(costFrame.Used * 8), p, 8, QueryResultFlags.Result64Bit | QueryResultFlags.ResultWaitBit);
            }
        }

        uint[]? vis = f.Visibility?.Results;
        Span<DrawCostRecord> span = System.Runtime.InteropServices.CollectionsMarshal.AsSpan(costFrame.Draws);
        for (int i = 0; i < span.Length; i++)
        {
            (int first, int count, int v) = costFrame.Queries[i];
            if (v >= 0)
            {
                span[i].Samples = vis != null && v < vis.Length ? vis[v] : -1;
            }
            else if (count > 0)
            {
                ulong s = 0;
                for (int q = first; q < first + count; q++)
                {
                    s += costFrame.Results[q];
                }

                span[i].Samples = (long)s;
            }
        }
        CostSink(costFrame.Draws);
        costFrame.Draws.Clear();
        costFrame.Queries.Clear();
        costFrame.Used = 0;
    }

    private void DestroyCost(Frame f)
    {
        if (f.Cost is { } c)
        {
            _vk.DestroyQueryPool(_dev, c.Pool, null);
        }

        f.Cost = null;
    }
}
