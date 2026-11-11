using System.Collections.Concurrent;
using VitaSharp.Core.Gpu;

namespace VitaSharp.Tests;

/// <summary>A backend swap (state load, LoadExec) runs where <see cref="GpuQueue.ReplaceOn"/> says: the desktop window
/// thread, which owns the Vulkan surface (made and destroyed elsewhere, the NVIDIA driver corrupted the native heap).</summary>
[Collection("guest")]
public sealed class GpuQueueReplaceTests(GuestMemoryFixture fixture)
{
    private sealed class ThreadBackend : IGpuBackend
    {
        public int CreatedOn = Environment.CurrentManagedThreadId, DisposedOn;
        public long PresentCount => 0;
        public byte[]? ExportDumpState() => null;
        public void Execute(GpuCommand command)
        {
        }
        public void Dispose() => DisposedOn = Environment.CurrentManagedThreadId;
    }

    [Fact]
    public void ReplacementRunsOnTheSurfaceThread()
    {
        _ = fixture;
        using var work = new BlockingCollection<Action>();
        var owner = new Thread(() =>
        {
            foreach (Action a in work.GetConsumingEnumerable())
            {
                a();
            }
        })
        { IsBackground = true };
        owner.Start();
        using var queue = new GpuQueue();
        var first = new ThreadBackend();
        queue.Backend = first;
        queue.ReplaceOn = swap =>
        {
            using var done = new ManualResetEventSlim();
            work.Add(() => { swap(); done.Set(); });
            done.Wait();
        };
        ThreadBackend? second = null;
        queue.ReplaceBackend(() => second = new ThreadBackend());
        work.CompleteAdding();
        Assert.Equal(owner.ManagedThreadId, first.DisposedOn);
        Assert.NotNull(second);
        Assert.Equal(owner.ManagedThreadId, second!.CreatedOn);
        Assert.Same(second, queue.Backend);
    }
}
