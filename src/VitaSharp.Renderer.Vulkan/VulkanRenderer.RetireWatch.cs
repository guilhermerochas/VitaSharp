using Silk.NET.Vulkan;
using System.Diagnostics;
using VitaSharp.Core.Util;

namespace VitaSharp.Renderer.Vulkan;

/// <summary>
/// How soon a frame the GPU finished gets published. A frame's results (render-target writebacks, visibility results)
/// and the sync points behind it (GXM notifications, display queue entries) reach the guest when the GPU worker
/// retires the frame, and the worker only checks its fences when it starts a scene or a present, or when its queue ran
/// empty (<see cref="PollRetire"/>, <see cref="Idle"/>). While it records a long scene the guest can sit waiting on a
/// notification the GPU already reached (Killzone on the Mali-G57: ~10 ms per frame in notification waits, with the
/// worker busy recording the next scene).
/// <para>
/// VITASHARP_RETIRE_WAKE (A/B): a watcher thread waits for each submitted frame with a fence of its own (an empty
/// batch submitted right after the frame: a fence signal operation of vkQueueSubmit covers every command submitted
/// earlier on the queue) and notes when the GPU finished it. "measure" only reports the delay between that moment and
/// the frame's retirement (PERF sync); "1" also lets the worker retire the frame at its next draw instead of its next
/// scene. Retiring records no GPU commands (writebacks are copied by the CPU from their readback buffers), so it is
/// safe between draws, as the texture path already does (<see cref="VulkanRenderer.PollRetire"/> from a texture read).
/// The order is unchanged: frames retire oldest first and each publishes its results before its signals. Dolphin and
/// PCSX2 have the same split: a thread that only waits on GPU fences, and the emulation thread acting on what it saw.
/// </para>
/// </summary>
public sealed unsafe partial class VulkanRenderer
{
    // read per renderer (not per process): tests run both modes in one process
    private readonly string? _retireWakeSetting = Environment.GetEnvironmentVariable(EnvVars.RetireWake)?.Trim();
    private bool RetireWatchOn => _retireWakeSetting is "1" or "measure";
    private bool RetireWakeOn => _retireWakeSetting == "1";

    /// <summary>Waits for submitted frames on its own fences and notes when the GPU finished each one.</summary>
    private FenceRetireWatcher? _retireWatcher;
    private long _lastRetiredSeq;
    private long _retireLatencyTicks, _retireLatencyMaxTicks, _retireLatencyCount, _retireWakePolls;

    /// <summary>After a frame's submission (queue lock held): the watcher's empty batch behind it.</summary>
    private void WatchSubmission(long seq)
    {
        if (!RetireWatchOn)
        {
            return;
        }

        _retireWatcher ??= new FenceRetireWatcher(new VkFenceOps(_vk, _dev));
        Fence fence = _retireWatcher.Take();
        var empty = new SubmitInfo { SType = StructureType.SubmitInfo };
        if (_vk.QueueSubmit(_core.Queue, 1, in empty, fence) == Result.Success)
        {
            _retireWatcher.Watch(fence, seq);
        }
    }

    /// <summary>A frame retired: how long after the GPU finished it (PERF sync).</summary>
    private void NoteRetired(Frame f)
    {
        _lastRetiredSeq = Math.Max(_lastRetiredSeq, f.SubmitSeq);
        if (_retireWatcher == null)
        {
            return;
        }

        long done = _retireWatcher.DoneTicks(f.SubmitSeq);
        if (done == 0)
        {
            return;
        }

        long late = Math.Max(0, Stopwatch.GetTimestamp() - done);
        _retireLatencyTicks += late;
        _retireLatencyMaxTicks = Math.Max(_retireLatencyMaxTicks, late);
        _retireLatencyCount++;
    }

    /// <summary>VITASHARP_RETIRE_WAKE=1: between draws, retire the frames the watcher saw finish.</summary>
    private void RetireWhatTheGpuFinished()
    {
        if (RetireWakeOn && _retireWatcher is { } watcher && watcher.CompletedSeq > _lastRetiredSeq)
        {
            _retireWakePolls++;
            PollRetire();
        }
    }

    private string RetireWatchReport() =>
        _retireWatcher == null ? "" :
        $"; GPU done -> published {Ms(_retireLatencyTicks) / Math.Max(1, _retireLatencyCount):F3} ms per submission (max {Ms(_retireLatencyMaxTicks):F1}, " +
        $"{Ms(_retireLatencyTicks) / Math.Max(1, _presentCount):F3} ms/frame, {_retireLatencyCount} submissions){(RetireWakeOn ? $", {_retireWakePolls} retired between draws" : "")}";
}
