using VitaSharp.Core.Gpu;

namespace VitaSharp.Tests;

/// <summary>
/// Taking the backend off the queue (<c>Backend = null</c>) and disposing it right after must never overlap the GPU
/// worker still inside that backend: the worker runs <see cref="IGpuBackend.Idle"/> whenever its queue runs empty, so
/// it is usually inside the renderer when a test or the host detaches it. Disposing a Vulkan renderer while its Idle
/// submits or waits on fences destroyed the device under the worker and killed the test host natively (no managed
/// exception) on a loaded machine, near the Piglet draw tests.
/// </summary>
public sealed class GpuQueueDetachTests
{
    /// <summary>The first Idle blocks until released; Dispose records whether it ran while an Idle was in progress.</summary>
    private sealed class IdleBlockingBackend : IGpuBackend
    {
        public readonly ManualResetEventSlim IdleEntered = new();
        public readonly ManualResetEventSlim IdleRelease = new();
        private int _idleCalls;
        private volatile bool _inIdle;
        public volatile bool DisposedDuringIdle;
        public volatile bool Disposed;

        public long PresentCount => 0;

        public byte[]? ExportDumpState() => null;

        public void Execute(GpuCommand command)
        {
        }

        public void Idle()
        {
            if (Interlocked.Increment(ref _idleCalls) != 1)
            {
                return;
            }

            _inIdle = true;
            IdleEntered.Set();
            IdleRelease.Wait();
            _inIdle = false;
        }

        public void Dispose()
        {
            if (_inIdle)
            {
                DisposedDuringIdle = true;
            }

            Disposed = true;
        }
    }

    [Fact]
    public void DetachingTheBackendWaitsForTheWorkerToLeaveIt()
    {
        using var queue = new GpuQueue();
        var backend = new IdleBlockingBackend();
        queue.Backend = backend;
        Assert.True(backend.IdleEntered.Wait(120_000), "the GPU worker never ran Idle on an empty queue");
        var detach = new Thread(() =>
        {
            queue.Backend = null;
            backend.Dispose();
        })
        {
            IsBackground = true,
            Name = "detach",
        };
        detach.Start();
        // the worker stays inside Idle until the detaching thread either finished (the old, racy setter) or is
        // blocked waiting for the worker (a setter that waits for it)
        SpinWait.SpinUntil(() => !detach.IsAlive || (detach.ThreadState & ThreadState.WaitSleepJoin) != 0);
        backend.IdleRelease.Set();
        Assert.True(detach.Join(120_000), "detaching the backend never returned after the worker left Idle");
        Assert.True(backend.Disposed);
        Assert.False(backend.DisposedDuringIdle, "the backend was disposed while the GPU worker was still inside its Idle");
        Assert.Null(queue.Backend);
    }

    [Fact]
    public void ABackendSwapFromTheSurfaceThreadStillCompletes()
    {
        // a swap runs its Dispose and factory on ReplaceOn's thread while the caller holds the worker's backend lock:
        // installing the new backend there must not wait for that lock again
        using var queue = new GpuQueue();
        var first = new IdleBlockingBackend();
        first.IdleRelease.Set();
        queue.Backend = first;
        var surfaceThreadDone = new ManualResetEventSlim();
        queue.ReplaceOn = swap =>
        {
            var surface = new Thread(() =>
            {
                swap();
                surfaceThreadDone.Set();
            })
            {
                IsBackground = true,
                Name = "surface",
            };
            surface.Start();
            Assert.True(surfaceThreadDone.Wait(120_000), "the swap on the surface thread deadlocked");
        };
        var second = new IdleBlockingBackend();
        second.IdleRelease.Set();
        queue.ReplaceBackend(() => second);
        Assert.True(first.Disposed);
        Assert.Same(second, queue.Backend);
        queue.Backend = null;
    }
}
