using Silk.NET.Vulkan;
using VitaSharp.Renderer.Vulkan;

namespace VitaSharp.Tests;

/// <summary>
/// The renderer's fence watcher (VITASHARP_RETIRE_WAKE) must not destroy its fences while its thread is still inside a
/// fence wait: the renderer destroys the device right after, and a watcher thread starved on a loaded machine past the
/// old one-second join was left inside vkWaitForFences on destroyed objects (an access violation in the test host,
/// seen twice in the suite). The fake's wait blocks until the test releases it, as a starved thread would stay there.
/// </summary>
public sealed class FenceRetireWatcherTests
{
    private sealed class BlockingFences : IFenceOps
    {
        public readonly ManualResetEventSlim WaitEntered = new();
        public readonly ManualResetEventSlim WaitRelease = new();
        private ulong _nextHandle = 1;
        private volatile bool _inWait;
        public volatile bool DestroyedDuringWait;
        public int Destroyed;

        public Fence Create() => new(_nextHandle++);

        public Result Wait(Fence fence, ulong timeoutNs)
        {
            _inWait = true;
            WaitEntered.Set();
            WaitRelease.Wait();
            _inWait = false;
            return Result.Timeout;
        }

        public void Reset(Fence fence)
        {
        }

        public void Destroy(Fence fence)
        {
            if (_inWait)
            {
                DestroyedDuringWait = true;
            }

            Interlocked.Increment(ref Destroyed);
        }
    }

    [Fact]
    public void DisposeWaitsForTheThreadToLeaveTheFenceWait()
    {
        var fences = new BlockingFences();
        var watcher = new FenceRetireWatcher(fences);
        watcher.Watch(watcher.Take(), 1);
        Assert.True(fences.WaitEntered.Wait(120_000), "the watcher thread never waited on the fence");
        var dispose = new Thread(watcher.Dispose) { IsBackground = true, Name = "dispose watcher" };
        dispose.Start();
        // the old Dispose gave up on the thread after one second and destroyed the fences under it; one that waits for
        // the thread is still blocked here, whatever the machine's speed (the outcome does not depend on this delay)
        dispose.Join(3000);
        fences.WaitRelease.Set();
        Assert.True(dispose.Join(120_000), "Dispose never returned after the fence wait ended");
        Assert.False(fences.DestroyedDuringWait, "the fences were destroyed while the watcher thread was still inside the fence wait");
        Assert.Equal(1, fences.Destroyed);
    }

    [Fact]
    public void AnIdleWatcherDisposesAtOnce()
    {
        var fences = new BlockingFences();
        var watcher = new FenceRetireWatcher(fences);
        Fence fence = watcher.Take();
        watcher.Dispose();
        Assert.Equal(1, fences.Destroyed);
        Assert.False(fences.DestroyedDuringWait);
        Assert.NotEqual(0ul, fence.Handle);
    }
}
