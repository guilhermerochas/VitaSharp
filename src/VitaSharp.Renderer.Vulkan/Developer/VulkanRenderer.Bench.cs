#if VITASHARP_DEVELOPER
using Silk.NET.Vulkan;
using VitaSharp.Core.Gpu;
using VitaSharp.Core.Gxm.Shader;
using VitaSharp.Core.Util;
using VitaSharp.Renderer.Vulkan.Developer;

namespace VitaSharp.Renderer.Vulkan;

/// <summary>
/// Developer build only (VITASHARP_DEVELOPER): the GPU bench hooks (<see cref="GpuBench"/>). A timestamp pair around
/// every scene while <see cref="GpuBenchState.Timing"/> is on, shader modules replaced by the files of
/// <see cref="GpuBenchState.ShaderDir"/>, and the depth load/store and barrier overrides of the bench parameters.
/// Nothing runs unless a bench set those switches.
/// </summary>
public sealed unsafe partial class VulkanRenderer
{
    const uint DevQueries = 8192;
    QueryPool _devPool;
    uint _devUsed;
    int _devOpen = -1;
    readonly List<(long Present, uint W, uint H, uint Rt, uint Query)> _devScenes = new();
    readonly Dictionary<(ulong, ulong), ModuleEntry?> _devShaders = new();

    /// <summary>One timed scene of <see cref="DevReadSceneTimes"/>.</summary>
    public readonly record struct BenchScene(long Present, int Index, uint Width, uint Height, uint RenderTarget, double Ms);

    void DevSceneBegin(BeginSceneCommand b)
    {
        if (!GpuBenchState.Timing || !TimestampsOk()) return;
        if (_devPool.Handle == 0)
        {
            var ci = new QueryPoolCreateInfo { SType = StructureType.QueryPoolCreateInfo, QueryType = QueryType.Timestamp, QueryCount = DevQueries };
            if (_vk.CreateQueryPool(_dev, in ci, null, out _devPool) != Result.Success) { _devPool = default; return; }
        }
        if (_devUsed + 2 > DevQueries) return;
        uint q = _devUsed;
        _devUsed += 2;
        // bottom of pipe at both ends: a scene's time runs from the end of the work before it to its own end (tilers
        // overlap the next pass's vertex work with this one's fragments, so a top-of-pipe start would count it twice)
        _vk.CmdResetQueryPool(F.Cmd, _devPool, q, 2);
        _vk.CmdWriteTimestamp(F.Cmd, PipelineStageFlags.BottomOfPipeBit, _devPool, q);
        _devOpen = _devScenes.Count;
        _devScenes.Add((_presentCount, b.Width, b.Height, b.Color?.Data ?? 0, q));
    }

    void DevSceneEnd()
    {
        if (_devOpen < 0) return;
        _vk.CmdWriteTimestamp(F.Cmd, PipelineStageFlags.BottomOfPipeBit, _devPool, _devScenes[_devOpen].Query + 1);
        _devOpen = -1;
    }

    /// <summary>The scene times since the last call (waits for them; call after a <see cref="FinishCommand"/>).</summary>
    public List<BenchScene> DevReadSceneTimes()
    {
        var res = new List<BenchScene>();
        if (_devUsed > 0)
        {
            var t = new ulong[_devUsed];
            fixed (ulong* p = t)
                if (_vk.GetQueryPoolResults(_dev, _devPool, 0, _devUsed, (nuint)(t.Length * 8), p, 8, QueryResultFlags.Result64Bit | QueryResultFlags.ResultWaitBit) == Result.Success)
                    for (int i = 0; i < _devScenes.Count; i++)
                    {
                        var s = _devScenes[i];
                        ulong a = t[s.Query], b = t[s.Query + 1];
                        res.Add(new BenchScene(s.Present, i, s.W, s.H, s.Rt, b >= a ? (b - a) * _timePeriodNs / 1e6 : double.NaN));
                    }
        }
        _devScenes.Clear();
        _devUsed = 0;
        _devOpen = -1;
        return res;
    }

    /// <summary>GPU time of the submits since the renderer started (the PERF gpu line's sum), in ms.</summary>
    public double DevGpuMsTotal => _gpuNs / 1e6;

    /// <summary>A module from <see cref="GpuBenchState.ShaderDir"/>: HASH_HINT.vert|frag (GLSL) or the same + .spv, or HASH.vert|frag[.spv]
    /// for every hint of the program. Null: none there (the translated shader is used).</summary>
    ModuleEntry? DevShaderOverride(GxpProgram program, ulong hintKey, Func<DrawCommand, uint, ShaderHints> hints, DrawCommand d, uint colorFormat)
    {
        var key = (program.Hash, hintKey);
        if (_devShaders.TryGetValue(key, out var e)) return e;
        string ext = program.IsVertex ? "vert" : "frag";
        string dir = GpuBenchState.ShaderDir!;
        string? file = null;
        foreach (var name in new[] { $"{program.Hash:X16}_{hintKey:X16}.{ext}", $"{program.Hash:X16}.{ext}" })
        {
            if (File.Exists(Path.Combine(dir, name + ".spv"))) { file = Path.Combine(dir, name + ".spv"); break; }
            if (File.Exists(Path.Combine(dir, name))) { file = Path.Combine(dir, name); break; }
        }
        if (file == null) { _devShaders[key] = null; return null; }
        // the translator's interface data (bindings, outputs) stays; only the code changes
        var gen = ShaderGenerator.Generate(program, hints(d, colorFormat));
        byte[]? spv;
        string? err = null;
        if (file.EndsWith(".spv", StringComparison.Ordinal)) spv = File.ReadAllBytes(file);
        else spv = _compiler.Compile(File.ReadAllText(file), program.IsVertex, out err);
        if (spv == null) throw new InvalidOperationException($"bench shader {file}: {err}");
        e = new ModuleEntry(CreateShaderModule(spv), gen);
        Log.Info($"[bench] shader {Path.GetFileName(file)} replaces {program.Hash:X16}/{hintKey:X16}");
        _devShaders[key] = e;
        return e;
    }

    /// <summary>Writes the GLSL of every translated shader as HASH_HINT.vert|frag (the files a variant edits).</summary>
    public int DevDumpShaders(string dir)
    {
        Directory.CreateDirectory(dir);
        int n = 0;
        foreach (var (k, m) in _modules)
            if (m.Gen is { Glsl.Length: > 0 } g)
            {
                File.WriteAllText(Path.Combine(dir, $"{k.Item1:X16}_{k.Item2:X16}.{(g.IsVertex ? "vert" : "frag")}"), g.Glsl);
                n++;
            }
        return n;
    }

    /// <summary>The bench's depth load/store overrides for one tiled rendering (FlushChain).</summary>
    static void DevRenderingOps(ref AttachmentLoadOp depthLoad, ref AttachmentStoreOp depthStore)
    {
        if (GpuBenchState.DepthLoad is { } l && depthLoad != AttachmentLoadOp.Load) depthLoad = l; // a loaded depth stays loaded
        if (GpuBenchState.DepthStore is { } s) depthStore = s;
    }

    /// <summary>bench barrier=full: a full memory barrier after every tiled rendering (what a barrier costs).</summary>
    void DevAfterRendering(CommandBuffer cmd)
    {
        if (!GpuBenchState.FullBarriers) return;
        var mb = new MemoryBarrier { SType = StructureType.MemoryBarrier, SrcAccessMask = AccessFlags.MemoryWriteBit, DstAccessMask = AccessFlags.MemoryReadBit | AccessFlags.MemoryWriteBit };
        _vk.CmdPipelineBarrier(cmd, PipelineStageFlags.AllCommandsBit, PipelineStageFlags.AllCommandsBit, 0, 1, &mb, 0, null, 0, null);
    }

    void DevDestroy()

    {
        foreach (var e in _devShaders.Values)
            if (e != null) _vk.DestroyShaderModule(_dev, e.Module, null);
        _devShaders.Clear();
        if (_devPool.Handle != 0) _vk.DestroyQueryPool(_dev, _devPool, null);
        _devPool = default;
    }
}
#endif
