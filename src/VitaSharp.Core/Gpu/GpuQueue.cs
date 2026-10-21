using System.Collections.Concurrent;
using VitaSharp.Core.Memory;
using VitaSharp.Core.Util;

namespace VitaSharp.Core.Gpu;

/// <summary>
/// Ordered queue of GPU work executed on a dedicated worker thread. Without a backend (headless runs) the
/// commands are simply retired, so notifications and sync points still advance and games keep running.
/// </summary>
public sealed class GpuQueue : IDisposable
{
    // ConcurrentQueue + SemaphoreSlim instead of BlockingCollection: a timed TryTake allocates a cancellation source per
    // poll, and the worker polls thousands of times per second
    readonly ConcurrentQueue<GpuCommand> _queue = new();
    readonly SemaphoreSlim _available = new(0);
    readonly Thread _worker;
    readonly object _progress = new();
    long _submitted;
    long _completed;
    volatile bool _stopping;

    public IGpuBackend? Backend { get; set; }

    /// <summary>Drains the queue, disposes the current backend and installs a new one (process exec).</summary>
    /// <summary>Held by the worker while it is inside the backend (Execute / Idle); backend replacement takes it too, so a
    /// renderer is never destroyed while the worker is still using it (crashed on LoadExec in windowed mode).</summary>
    readonly object _backendLock = new();

    public void ReplaceBackend(Func<IGpuBackend?>? factory)
    {
        WaitIdle();
        lock (_backendLock)
        {
            var old = Backend;
            Backend = null;
            old?.Dispose();
            if (factory != null) Backend = factory();
        }
    }

    /// <summary>Pulsed whenever a notification value is written (guest waiters re-check).</summary>
    public static readonly object NotificationSync = new();
    public object NotificationLock => NotificationSync;

    /// <summary>Writes a notification value and wakes guest waiters (used by backends once the GPU really finished).</summary>
    public static void SignalNotification(uint address, uint value)
    {
        lock (NotificationSync)
        {
            GuestMemory.W32(address, value);
            Monitor.PulseAll(NotificationSync);
        }
    }

    public GpuQueue()
    {
        _worker = new Thread(Run) { Name = "GPU worker", IsBackground = true };
        _worker.Start();
    }

    // Draw commands are recycled once the backend executed them (it copies what it needs during Execute); this keeps
    // the thousands of draws per frame from churning the garbage collector.
    readonly ConcurrentBag<DrawCommand> _drawPool = new();

    public DrawCommand RentDraw()
    {
        if (!_drawPool.TryTake(out var d)) d = new DrawCommand();
        d.Reset();
        return d;
    }

    public long Completed => Interlocked.Read(ref _completed);
    public long Submitted => Interlocked.Read(ref _submitted);

    public long Submit(GpuCommand cmd)
    {
        cmd.Sequence = Interlocked.Increment(ref _submitted);
        _queue.Enqueue(cmd);
        _available.Release();
        return cmd.Sequence;
    }

    public void WaitFor(long sequence)
    {
        using var blocked = Kernel.Scheduler.Block();
        lock (_progress)
            while (Completed < sequence && !_stopping) Monitor.Wait(_progress, 100);
    }

    public void WaitIdle() => WaitFor(Submitted);

    public int Pending => (int)(Submitted - Completed);

    void Run()
    {
        NativeThread.Register("GPU worker");
        while (!_stopping)
        {
            GpuCommand? cmd;
            try
            {
                if (!_queue.TryDequeue(out cmd))
                {
                    try { lock (_backendLock) Backend?.Idle(); } catch (Exception e) { Log.Error($"GPU backend idle failed: {e}"); }
                    if (!_available.Wait(50) || !_queue.TryDequeue(out cmd)) continue;
                }
                else _available.Wait(0);
            }
            catch (ObjectDisposedException) { break; }

            if (cmd is CallbackCommand pre)
            {
                // CPU work (transfers) happens first so the backend sees the memory it produced
                try { pre.Action(); }
                catch (Exception e) { Log.Error($"GPU callback failed: {e}"); }
            }
            try
            {
                lock (_backendLock) Backend?.Execute(cmd);
            }
            catch (Exception e)
            {
                Log.WarnOnce("gpu-" + cmd.GetType().Name + e.GetType().Name, $"GPU backend failed on {cmd.GetType().Name}: {e}");
            }

            switch (cmd)
            {
                case DrawCommand draw:
                    if (_drawPool.Count < 16384) _drawPool.Add(draw);
                    break;
                case SignalCommand signal:
                    bool deferred = false;
                    try { lock (_backendLock) deferred = Backend?.DeferSignal(signal.Signal) ?? false; }
                    catch (Exception e) { Log.Error($"GPU backend signal failed: {e}"); }
                    if (!deferred) signal.Signal();
                    break;
                case EndSceneCommand end:
                    if (end.Notifications.Length > 0 && !end.NotificationsDeferred)
                    {
                        lock (NotificationLock)
                        {
                            foreach (var (addr, value) in end.Notifications) GuestMemory.W32(addr, value);
                            Monitor.PulseAll(NotificationLock);
                        }
                    }
                    break;
            }

            lock (_progress)
            {
                Interlocked.Exchange(ref _completed, cmd.Sequence);
                Monitor.PulseAll(_progress);
            }
        }
    }

    /// <summary>
    /// Writes a notification in GPU order: after all previously submitted work completed and its results (writebacks,
    /// visibility results) are in guest memory.
    /// </summary>
    public void Notify(uint address, uint value) => Submit(new SignalCommand { Signal = () => SignalNotification(address, value) });

    /// <summary>A sync point the host can wait on: set once all work submitted before it is complete and published.</summary>
    public sealed class Fence
    {
        bool _done;
        public bool Done { get { lock (this) return _done; } }
        public void Set() { lock (this) { _done = true; Monitor.PulseAll(this); } }
        /// <summary>Waits until set (or <paramref name="keepWaiting"/> turns false).</summary>
        public void Wait(Func<bool> keepWaiting)
        {
            lock (this)
                while (!_done && keepWaiting()) Monitor.Wait(this, 50);
        }
    }

    public Fence SubmitFence()
    {
        var f = new Fence();
        Submit(new SignalCommand { Signal = f.Set });
        return f;
    }

    public void Dispose()
    {
        _stopping = true;
        lock (_progress) Monitor.PulseAll(_progress);
        _worker.Join(2000);
        _available.Dispose();
    }
}
