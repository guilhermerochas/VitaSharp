using Silk.NET.Vulkan;
using System.Collections.Concurrent;
using System.Diagnostics;

namespace VitaSharp.Renderer.Vulkan;

/// <summary>The fence calls of <see cref="FenceRetireWatcher"/> (Vulkan in the renderer, a fake in the tests).</summary>
internal interface IFenceOps
{
    Fence Create();

    /// <summary>vkWaitForFences on one fence with a timeout in nanoseconds.</summary>
    Result Wait(Fence fence, ulong timeoutNs);

    void Reset(Fence fence);

    void Destroy(Fence fence);
}

/// <summary>The device's fence calls.</summary>
internal sealed unsafe class VkFenceOps(Vk vk, Device dev) : IFenceOps
{
    public Fence Create()
    {
        var fci = new FenceCreateInfo { SType = StructureType.FenceCreateInfo };
        vk.CreateFence(dev, in fci, null, out Fence fence).Ok("vkCreateFence (retire watcher)");
        return fence;
    }

    public Result Wait(Fence fence, ulong timeoutNs) => vk.WaitForFences(dev, 1, in fence, true, timeoutNs);

    public void Reset(Fence fence) => vk.ResetFences(dev, 1, in fence);

    public void Destroy(Fence fence) => vk.DestroyFence(dev, fence, null);
}

/// <summary>
/// VITASHARP_RETIRE_WAKE: a thread that waits on a fence behind each submission and records when the GPU finished it.
/// <see cref="Dispose"/> waits for the thread to leave the fence wait before destroying the fences (and the renderer
/// the device after it): with a one-second join, a thread starved on a loaded machine was still inside
/// vkWaitForFences when its fences and then the device were destroyed (an access violation in the test host). The
/// thread checks the stop flag after every wait (at most <see cref="WaitSliceNs"/>), so the join ends.
/// </summary>
internal sealed class FenceRetireWatcher : IDisposable
{
    private const int Slots = 64;
    /// <summary>One fence wait: the thread looks at the stop flag between waits.</summary>
    internal const ulong WaitSliceNs = 50_000_000;
    private readonly IFenceOps _fences;
    private readonly ConcurrentQueue<(Fence Fence, long Seq)> _pending = new();
    private readonly ConcurrentBag<Fence> _free = new();
    private readonly List<Fence> _all = new();
    private readonly SemaphoreSlim _work = new(0);
    private readonly long[] _doneTicks = new long[Slots];
    private readonly long[] _doneSeq = new long[Slots];
    private readonly Thread _thread;
    private volatile bool _stop;
    private long _completedSeq;

    public FenceRetireWatcher(IFenceOps fences)
    {
        _fences = fences;
        _thread = new Thread(Run) { IsBackground = true, Name = "GPU fence watcher" };
        _thread.Start();
    }

    public long CompletedSeq => Volatile.Read(ref _completedSeq);

    public Fence Take()
    {
        if (_free.TryTake(out Fence fence))
        {
            return fence;
        }

        fence = _fences.Create();
        lock (_all)
        {
            _all.Add(fence);
        }

        return fence;
    }

    public void Watch(Fence fence, long seq)
    {
        _pending.Enqueue((fence, seq));
        _work.Release();
    }

    public long DoneTicks(long seq)
    {
        int slot = (int)(seq % Slots);
        return Volatile.Read(ref _doneSeq[slot]) == seq ? Volatile.Read(ref _doneTicks[slot]) : 0;
    }

    private void Run()
    {
        while (!_stop)
        {
            _work.Wait(100);
            while (!_stop && _pending.TryPeek(out (Fence Fence, long Seq) item))
            {
                Result result = _fences.Wait(item.Fence, WaitSliceNs);
                if (result == Result.Timeout)
                {
                    continue;
                }

                _pending.TryDequeue(out _);
                if (result != Result.Success)
                {
                    return; // device lost: the worker reports it
                }

                int slot = (int)(item.Seq % Slots);
                Volatile.Write(ref _doneTicks[slot], Stopwatch.GetTimestamp());
                Volatile.Write(ref _doneSeq[slot], item.Seq);
                Volatile.Write(ref _completedSeq, item.Seq);
                _fences.Reset(item.Fence);
                _free.Add(item.Fence);
            }
        }
    }

    public void Dispose()
    {
        _stop = true;
        _work.Release();
        // no time limit: the fences (and, after this, the device) must outlive the thread's last wait
        _thread.Join();
        lock (_all)
        {
            foreach (Fence fence in _all)
            {
                _fences.Destroy(fence);
            }

            _all.Clear();
        }
    }
}
