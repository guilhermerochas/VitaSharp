using System.Diagnostics;
using Silk.NET.Vulkan;
using VitaSharp.Core.Gpu;
using VitaSharp.Core.Util;

namespace VitaSharp.Renderer.Vulkan;

/// <summary>
/// GPU→CPU synchronisation: when submitted frames are retired (their fence waited on and their results published
/// into guest memory), and the PERF sync counters that show how long the GPU worker was blocked doing so.
/// <para>
/// Results (render-target writebacks, visibility results, deferred notifications) are published as soon as the GPU
/// finished the frame: <see cref="PollRetire"/> checks the fences without waiting at scene starts and presents, and
/// <see cref="Idle"/> waits for them only while no other work is queued. Every sync point the guest can wait on is a
/// deferred signal (<see cref="DeferSignal"/>), so it still fires only after all earlier results are in memory.
/// </para>
/// </summary>
public sealed unsafe partial class VulkanRenderer
{
    GpuQueue? _queue;

    public void Attach(GpuQueue queue) => _queue = queue;

    /// <summary>Why a frame is retired (fence wait + publication of its results).</summary>
    enum RetireCause { Slot, Idle, Present, Finish, Older, Dispose, Poll, Memory }

    const int RetireCauses = 8;
    readonly long[] _retireCount = new long[RetireCauses];
    readonly long[] _retireBlocked = new long[RetireCauses];   // retirements whose fence had not signalled yet
    readonly long[] _retireWaitTicks = new long[RetireCauses];
    long _idleWaitTicks, _idleYields;
    long _wbCount, _wbBytes, _wbTicks;

    static readonly bool TraceSync = Environment.GetEnvironmentVariable("VITASHARP_TRACE_SYNC") == "1";
    (RetireCause Cause, double Ms)? _longWait;

    /// <summary>Waits for the frame's fence, counting the time spent blocked.</summary>
    void WaitFrameFence(Frame f, RetireCause cause)
    {
        int c = (int)cause;
        _retireCount[c]++;
        if (_vk.GetFenceStatus(_dev, f.Fence) == Result.Success) return;
        long t0 = Stopwatch.GetTimestamp();
        if (_vk.WaitForFences(_dev, 1, in f.Fence, true, ulong.MaxValue) == Result.ErrorDeviceLost) { _deviceLost = true; ReportDeviceLost("vkWaitForFences"); }
        FrameLedger.AddSince(FrameLedger.Cat.FenceWait, t0);
        _retireBlocked[c]++;
        long dt = Stopwatch.GetTimestamp() - t0;
        _retireWaitTicks[c] += dt;
        if (TraceSync && Ms(dt) >= 20) _longWait = (cause, Ms(dt));
    }

    /// <summary>The oldest submitted frame that has not been retired (frames retire in submission order).</summary>
    Frame? OldestInFlight()
    {
        Frame? oldest = null;
        foreach (var f in _frames)
            if (f.Submitted && !f.Retired && (oldest == null || f.SubmitSeq < oldest.SubmitSeq)) oldest = f;
        return oldest;
    }

    bool InFlightResults()
    {
        foreach (var f in _frames)
            if (f.Submitted && !f.Retired && HasPendingResults(f)) return true;
        return false;
    }

    /// <summary>Publishes the results of every frame the GPU has already finished, oldest first, without waiting.</summary>
    void PollRetire()
    {
        while (OldestInFlight() is { } f && _vk.GetFenceStatus(_dev, f.Fence) == Result.Success) RetireFrame(f, RetireCause.Poll);
    }

    /// <summary>
    /// The queue ran empty: submit what is recorded when the guest may be waiting for it, then publish finished
    /// frames. While results are still on the GPU the worker waits for them in 1 ms slices and returns as soon as new
    /// work is queued, so that work is recorded and reaches the GPU instead of queueing behind the fence (the guest
    /// keeps producing the next frame while the GPU renders this one).
    /// </summary>
    public void Idle()
    {
        // keep latency low when the guest stops producing work (loading screens etc.)
        bool resultsRecorded = F.Recording && HasPendingResults(F);
        if (F.Recording && (!_inScene || resultsRecorded) && (_drawsSinceSubmit > 0 || resultsRecorded))
        {
            if (TraceVis) Log.Info($"[vis] idle submit inScene={_inScene} rt=0x{_rt?.Address ?? 0:X8} present={_presentCount} draws={_drawSerial}");
            SubmitFrame(false, 0); _drawsSinceSubmit = 0;
        }
        PollRetire();
        long t0 = Stopwatch.GetTimestamp();
        try
        {
            while (InFlightResults())
            {
                if (_queue is { Pending: > 0 }) { _idleYields++; return; }
                var f = OldestInFlight()!;
                var r = _vk.WaitForFences(_dev, 1, in f.Fence, true, 1_000_000);
                if (r == Result.Success) RetireFrame(f, RetireCause.Idle);
                else if (r == Result.ErrorDeviceLost) { _deviceLost = true; ReportDeviceLost("vkWaitForFences (idle)"); return; }
                else if (Ms(Stopwatch.GetTimestamp() - t0) > 50) return; // the queue loop calls again (and can stop)
            }
        }
        finally { _idleWaitTicks += Stopwatch.GetTimestamp() - t0; }
    }

    /// <summary>
    /// A frame was just submitted with a present: publish what has finished. A flipped frame's results must be in
    /// memory by the next flip at the latest (games read luminance and picking buffers a frame later without
    /// waiting); sync points after the flip are deferred signals and wait for them anyway.
    /// </summary>
    void RetireFlippedFrame()
    {
        PollRetire();
        if (_flipSeq is long seq)
            while (OldestInFlight() is { } f && f.SubmitSeq <= seq) RetireFrame(f, RetireCause.Present);
        var just = _frames[(_frameIndex + FramesInFlight - 1) % FramesInFlight];
        _flipSeq = just.Submitted && !just.Retired && HasPendingResults(just) ? just.SubmitSeq : null;
    }

    long? _flipSeq;

    string SyncReport()
    {
        double frames = Math.Max(1, _presentCount);
        var sb = new System.Text.StringBuilder("PERF sync: blocking fence waits (blocked/retired, ms)");
        long total = 0;
        for (int i = 0; i < RetireCauses; i++)
        {
            total += _retireWaitTicks[i];
            if (_retireCount[i] == 0) continue;
            sb.Append($" {(RetireCause)i} {_retireBlocked[i]}/{_retireCount[i]} {Ms(_retireWaitTicks[i]):F0};");
        }
        sb.Append($" total {Ms(total) / frames:F3} ms/frame; idle (queue empty) {Ms(_idleWaitTicks) / frames:F3} ms/frame, {_idleYields} yields to new work;");
        sb.Append($" {_submitSeq / frames:F2} submits/frame; writebacks {_wbCount / frames:F2}/frame ({_wbBytes / frames / 1024:F0} KB/frame, {Ms(_wbTicks) / frames:F3} ms/frame)");
        return sb.ToString();
    }
}
