using Silk.NET.Vulkan;
using VitaSharp.Core.Gpu;
using VitaSharp.Core.Memory;
using VitaSharp.Core.Util;

namespace VitaSharp.Renderer.Vulkan;

/// <summary>
/// GXM visibility tests: draws with a visibility index are wrapped in occlusion queries, and once the frame's GPU
/// work has completed the sample counts are written into the guest visibility buffer (INCREMENT: the number of
/// samples that passed, summed over the draws sharing an index; SET: 1 when any sample passed). Above (or below) native
/// resolution the counts are in guest pixels: a scene at internal scale k has k*k attachment pixels per guest pixel, so
/// its counts are divided by k*k (the game compares them with the area it expects, e.g. Uncharted's sun flare points).
/// </summary>
public sealed unsafe partial class VulkanRenderer
{
    private const uint VisQueriesPerFrame = 8192;
    private static readonly bool s_traceVis = Environment.GetEnvironmentVariable(EnvVars.TraceVis) == "1";
    /// <summary>Debug: report every visibility test as passed (VITASHARP_VIS_FORCE=1) to tell culling problems apart.</summary>
    private static readonly bool s_forceVisible = Environment.GetEnvironmentVariable(EnvVars.VisForce) == "1";

    private sealed class VisibilityFrame
    {
        public QueryPool Pool;
        public uint Used;
        /// <summary>The result word of each query, in the address space of the process that drew.</summary>
        public readonly List<(uint Address, bool Increment, AddressSpace? Space, float Area)> Queries = new();
        /// <summary>Reused per completion (no per-frame garbage): query results and per-address sums.</summary>
        public uint[] Results = [];
        public readonly Dictionary<(AddressSpace?, uint), (double Samples, bool Increment)> Sums = new();
    }

    private VisibilityFrame Vis(Frame f)
    {
        if (f.Visibility != null)
        {
            return f.Visibility;
        }

        var ci = new QueryPoolCreateInfo { SType = StructureType.QueryPoolCreateInfo, QueryType = QueryType.Occlusion, QueryCount = VisQueriesPerFrame };
        var v = new VisibilityFrame();
        _vk.CreateQueryPool(_dev, in ci, null, out v.Pool);
        f.Visibility = v;
        return v;
    }

    /// <summary>At the start of a frame's command buffer (outside any rendering): make its queries reusable.</summary>
    private void ResetVisibility(Frame f)
    {
        VisibilityFrame visibilityFrame = Vis(f);
        _vk.CmdResetQueryPool(f.Cmd, visibilityFrame.Pool, 0, VisQueriesPerFrame);
        visibilityFrame.Used = 0;
        visibilityFrame.Queries.Clear();
    }

    /// <summary>Starts the occlusion query for a draw with a visibility test; returns false when none is needed.</summary>
    private bool BeginVisibilityQuery(CommandBuffer cmd, DrawCommand d, out uint query)
    {
        query = 0;
        if (d.VisAddress == 0)
        {
            return false;
        }

        VisibilityFrame visibilityFrame = Vis(F);
        if (visibilityFrame.Used >= VisQueriesPerFrame)
        {
            Log.WarnOnce("visq-full", "[gpu] visibility queries per frame exhausted");
            return false;
        }
        query = visibilityFrame.Used++;
        float k = RtScale; // internal resolution only: the supersampled MSAA path stands for GXM's own samples
        visibilityFrame.Queries.Add((d.VisAddress, d.VisIncrement, d.Space, k * k));
        QueryControlFlags flags = (d.VisIncrement || CostOn) && _core.OcclusionQueryPrecise ? QueryControlFlags.PreciseBit : 0;
        _vk.CmdBeginQuery(cmd, visibilityFrame.Pool, query, flags);
        return true;
    }

    private void EndVisibilityQuery(CommandBuffer cmd, uint query) => _vk.CmdEndQuery(cmd, Vis(F).Pool, query);

    /// <summary>Runs after the frame's fence signalled: writes the results into guest memory.</summary>
    private void CompleteVisibility(Frame f)
    {
        VisibilityFrame? visibilityFrame = f.Visibility;
        if (visibilityFrame == null || visibilityFrame.Used == 0)
        {
            return;
        }

        if (visibilityFrame.Results.Length < visibilityFrame.Used)
        {
            visibilityFrame.Results = new uint[Math.Max(visibilityFrame.Used, Math.Min(visibilityFrame.Results.Length * 2, VisQueriesPerFrame))];
        }

        uint[] results = visibilityFrame.Results;
        fixed (uint* p = results)
        {
            _vk.GetQueryPoolResults(_dev, visibilityFrame.Pool, 0, visibilityFrame.Used, (nuint)(visibilityFrame.Used * 4), p, 4, QueryResultFlags.ResultWaitBit);
        }

        Dictionary<(AddressSpace?, uint), (double Samples, bool Increment)> sums = visibilityFrame.Sums;
        sums.Clear();
        for (int i = 0; i < visibilityFrame.Queries.Count; i++)
        {
            (uint addr, bool inc, AddressSpace? space, float area) = visibilityFrame.Queries[i];
            (double Samples, bool Increment) cur = sums.GetValueOrDefault((space, addr));
            sums[(space, addr)] = (cur.Samples + (s_forceVisible ? 1u << 16 : area == 1f ? results[i] : results[i] / (double)area), inc);
        }
        foreach (((AddressSpace? space, uint addr), (double samples, bool inc)) in sums)
        {
            using (AddressSpace.Use(space))
            {
                GuestMemory.W32(addr, inc ? (uint)Math.Min(samples > 0 ? Math.Max(1, Math.Round(samples)) : 0, uint.MaxValue) : samples > 0 ? 1u : 0u);
            }
        }

        if (s_traceVis)
        {
            Log.Info($"[vis] retire f{VitaSharp.Core.Hle.Modules.Display.FramesPresented} gpuframe={FrameCount} wrote {sums.Count} words, nonzero: " + string.Join(" ", sums.Where(k => k.Value.Samples > 0).OrderBy(k => k.Key.Item2).Select(k => $"{k.Key.Item2:X}={k.Value.Samples}")));
        }

        visibilityFrame.Used = 0;
        visibilityFrame.Queries.Clear();
    }

    private void DestroyVisibility(Frame f)
    {
        if (f.Visibility is { } v)
        {
            _vk.DestroyQueryPool(_dev, v.Pool, null);
        }

        f.Visibility = null;
    }
}
