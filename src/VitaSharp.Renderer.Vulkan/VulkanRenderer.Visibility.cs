using Silk.NET.Vulkan;
using VitaSharp.Core.Gpu;
using VitaSharp.Core.Memory;
using VitaSharp.Core.Util;

namespace VitaSharp.Renderer.Vulkan;

/// <summary>
/// GXM visibility tests: draws with a visibility index are wrapped in occlusion queries, and once the frame's GPU
/// work has completed the sample counts are written into the guest visibility buffer (INCREMENT: the number of
/// samples that passed, summed over the draws sharing an index; SET: 1 when any sample passed).
/// </summary>
public sealed unsafe partial class VulkanRenderer
{
    const uint VisQueriesPerFrame = 8192;
    static readonly bool TraceVis = Environment.GetEnvironmentVariable(EnvVars.TraceVis) == "1";
    /// <summary>Debug: report every visibility test as passed (VITASHARP_VIS_FORCE=1) to tell culling problems apart.</summary>
    static readonly bool ForceVisible = Environment.GetEnvironmentVariable(EnvVars.VisForce) == "1";

    sealed class VisibilityFrame
    {
        public QueryPool Pool;
        public uint Used;
        /// <summary>The result word of each query, in the address space of the process that drew.</summary>
        public readonly List<(uint Address, bool Increment, AddressSpace? Space)> Queries = new();
        /// <summary>Reused per completion (no per-frame garbage): query results and per-address sums.</summary>
        public uint[] Results = [];
        public readonly Dictionary<(AddressSpace?, uint), (ulong Samples, bool Increment)> Sums = new();
    }

    VisibilityFrame Vis(Frame f)
    {
        if (f.Visibility != null) return f.Visibility;
        var ci = new QueryPoolCreateInfo { SType = StructureType.QueryPoolCreateInfo, QueryType = QueryType.Occlusion, QueryCount = VisQueriesPerFrame };
        var v = new VisibilityFrame();
        _vk.CreateQueryPool(_dev, in ci, null, out v.Pool);
        f.Visibility = v;
        return v;
    }

    /// <summary>At the start of a frame's command buffer (outside any rendering): make its queries reusable.</summary>
    void ResetVisibility(Frame f)
    {
        var v = Vis(f);
        _vk.CmdResetQueryPool(f.Cmd, v.Pool, 0, VisQueriesPerFrame);
        v.Used = 0;
        v.Queries.Clear();
    }

    /// <summary>Starts the occlusion query for a draw with a visibility test; returns false when none is needed.</summary>
    bool BeginVisibilityQuery(DrawCommand d, out uint query)
    {
        query = 0;
        if (d.VisAddress == 0) return false;
        var v = Vis(F);
        if (v.Used >= VisQueriesPerFrame) { Log.WarnOnce("visq-full", "[gpu] visibility queries per frame exhausted"); return false; }
        query = v.Used++;
        v.Queries.Add((d.VisAddress, d.VisIncrement, d.Space));
        var flags = d.VisIncrement && _core.OcclusionQueryPrecise ? QueryControlFlags.PreciseBit : 0;
        _vk.CmdBeginQuery(Cmd, v.Pool, query, flags);
        return true;
    }

    void EndVisibilityQuery(uint query) => _vk.CmdEndQuery(Cmd, Vis(F).Pool, query);

    /// <summary>Runs after the frame's fence signalled: writes the results into guest memory.</summary>
    void CompleteVisibility(Frame f)
    {
        var v = f.Visibility;
        if (v == null || v.Used == 0) return;
        if (v.Results.Length < v.Used) v.Results = new uint[Math.Max(v.Used, Math.Min(v.Results.Length * 2, VisQueriesPerFrame))];
        var results = v.Results;
        fixed (uint* p = results)
            _vk.GetQueryPoolResults(_dev, v.Pool, 0, v.Used, (nuint)(v.Used * 4), p, 4, QueryResultFlags.ResultWaitBit);
        var sums = v.Sums;
        sums.Clear();
        for (int i = 0; i < v.Queries.Count; i++)
        {
            var (addr, inc, space) = v.Queries[i];
            var cur = sums.GetValueOrDefault((space, addr));
            sums[(space, addr)] = (cur.Samples + (ForceVisible ? 1u << 16 : results[i]), inc);
        }
        foreach (var ((space, addr), (samples, inc)) in sums)
            using (AddressSpace.Use(space)) GuestMemory.W32(addr, inc ? (uint)Math.Min(samples, uint.MaxValue) : samples > 0 ? 1u : 0u);
        if (TraceVis) Log.Info($"[vis] retire f{VitaSharp.Core.Hle.Modules.Display.FramesPresented} gpuframe={FrameCount} wrote {sums.Count} words, nonzero: " + string.Join(" ", sums.Where(k => k.Value.Samples > 0).OrderBy(k => k.Key.Item2).Select(k => $"{k.Key.Item2:X}={k.Value.Samples}")));
        v.Used = 0;
        v.Queries.Clear();
    }

    void DestroyVisibility(Frame f)
    {
        if (f.Visibility is { } v) _vk.DestroyQueryPool(_dev, v.Pool, null);
        f.Visibility = null;
    }
}
