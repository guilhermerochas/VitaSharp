using Silk.NET.Vulkan;

namespace VitaSharp.Renderer.Vulkan;

/// <summary>
/// GPU execution time (PERF gpu line): a timestamp at the start and at the end of every submitted command buffer,
/// read back when the frame retires. CPU-side numbers (GPU worker ms/frame) say nothing about how long the GPU
/// itself was busy, which is what render-path changes such as MSAA emulation affect.
/// </summary>
public sealed unsafe partial class VulkanRenderer
{
    QueryPool _timePool;
    bool _timeChecked, _timeOk;
    double _timePeriodNs;
    double _gpuNs;
    double _lastGpuMs;
    long _gpuSubmits;

    bool TimestampsOk()
    {
        if (_timeChecked) return _timeOk;
        _timeChecked = true;
        uint n = 0;
        _vk.GetPhysicalDeviceQueueFamilyProperties(_core.Physical, ref n, null);
        var fam = new QueueFamilyProperties[n];
        fixed (QueueFamilyProperties* p = fam) _vk.GetPhysicalDeviceQueueFamilyProperties(_core.Physical, ref n, p);
        if (!_core.Timestamps || _core.QueueFamily >= n || fam[_core.QueueFamily].TimestampValidBits == 0 || _core.Properties.Limits.TimestampPeriod <= 0) return false;
        _timePeriodNs = _core.Properties.Limits.TimestampPeriod;
        var ci = new QueryPoolCreateInfo { SType = StructureType.QueryPoolCreateInfo, QueryType = QueryType.Timestamp, QueryCount = FramesInFlight * 2 };
        _timeOk = _vk.CreateQueryPool(_dev, in ci, null, out _timePool) == Result.Success;
        return _timeOk;
    }

    /// <summary>Start of a frame's command buffer (outside any rendering).</summary>
    void BeginGpuTime(Frame f)
    {
        if (!TimestampsOk()) return;
        uint q = (uint)_frameIndex * 2;
        _vk.CmdResetQueryPool(f.Cmd, _timePool, q, 2);
        _vk.CmdWriteTimestamp(f.Cmd, PipelineStageFlags.TopOfPipeBit, _timePool, q);
        f.TimeQuery = (int)q;
    }

    /// <summary>End of a frame's command buffer, just before it is closed.</summary>
    void EndGpuTime(Frame f)
    {
        if (f.TimeQuery < 0) return;
        _vk.CmdWriteTimestamp(f.Cmd, PipelineStageFlags.BottomOfPipeBit, _timePool, (uint)f.TimeQuery + 1);
        f.TimePending = true;
    }

    /// <summary>The frame's fence signalled: add its GPU time.</summary>
    void CompleteGpuTime(Frame f)
    {
        if (!f.TimePending) return;
        f.TimePending = false;
        var t = stackalloc ulong[2];
        if (_vk.GetQueryPoolResults(_dev, _timePool, (uint)f.TimeQuery, 2, 16, t, 8, QueryResultFlags.Result64Bit) == Result.Success && t[1] >= t[0])
        {
            _lastGpuMs = (t[1] - t[0]) * _timePeriodNs / 1e6;
            _gpuNs += (t[1] - t[0]) * _timePeriodNs;
            _gpuSubmits++;
        }
        f.TimeQuery = -1;
    }

    string GpuTimeReport()
    {
        if (!_timeOk || _presentCount == 0) return $"PERF gpu: no timestamps; {LoadStoreReport()}";
        return $"PERF gpu: {_gpuNs / 1e6 / _presentCount:F3} ms GPU time per presented frame ({_gpuSubmits} submits, {_gpuNs / 1e6 / Math.Max(1, _gpuSubmits):F3} ms each), MSAA {MsaaReport()}; {LoadStoreReport()}" + (_statTw1Scenes > 0 ? $"; {TileWord1Report()}" : "");
    }

    void DestroyGpuTime()
    {
        if (_timePool.Handle != 0) _vk.DestroyQueryPool(_dev, _timePool, null);
        _timePool = default;
    }
}
