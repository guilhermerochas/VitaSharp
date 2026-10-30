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

    IGpuBackend? _backend;
    public IGpuBackend? Backend { get => _backend; set { _backend = value; value?.Attach(this); } }

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

    /// <summary>Writes a notification value in <paramref name="space"/> (the submitting process's) and wakes guest
    /// waiters (used by backends once the GPU really finished).</summary>
    public static void SignalNotification(AddressSpace? space, uint address, uint value)
    {
        lock (NotificationSync)
        {
            using (AddressSpace.Use(space ?? AddressSpace.Current)) GuestMemory.W32(address, value);
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
        d.Space = null;
        return d;
    }

    public long Completed => Interlocked.Read(ref _completed);
    public long Submitted => Interlocked.Read(ref _submitted);

    // ---- several processes on one GPU: a scene (BeginScene .. EndScene) is one process's at a time. The backend records
    // a scene's draws into the render pass BeginScene opened, so another process's commands must not land in between:
    // while a process has a scene open, the other processes' submissions wait (the GPU is busy with that scene).
    readonly object _sceneLock = new();
    int _sceneOwner;

    /// <summary>Set once a second process shares this GPU; until then submissions skip the scene arbitration.</summary>
    public volatile bool SharedByProcesses;

    /// <summary>True while process <paramref name="pid"/> has a scene open (its threads are not parked then: see
    /// <see cref="Kernel.GuestThread"/> suspension).</summary>
    public bool HasOpenScene(int pid) => pid != 0 && Volatile.Read(ref _sceneOwner) == pid;

    /// <summary>A process that ends with a scene open gives the GPU back.</summary>
    public void ReleaseScene(int pid)
    {
        lock (_sceneLock)
        {
            if (_sceneOwner != pid) return;
            _sceneOwner = 0;
            Monitor.PulseAll(_sceneLock);
        }
    }

    /// <summary>Queues <paramref name="cmd"/> once no other process has a scene open (the caller is the current
    /// process; host-side work, pid 0, is never held back) and tracks the scene it opens or closes.</summary>
    long SubmitArbitrated(GpuCommand cmd)
    {
        int pid = Kernel.ProcessScope.CurrentPid;
        lock (_sceneLock)
        {
            if (pid != 0 && _sceneOwner != 0 && _sceneOwner != pid)
            {
                using var blocked = Kernel.Scheduler.Block();
                while (_sceneOwner != 0 && _sceneOwner != pid && !_stopping) Monitor.Wait(_sceneLock, 50);
            }
            long seq = Enqueue(cmd);
            if (pid != 0 && cmd is BeginSceneCommand) _sceneOwner = pid;
            else if (cmd is EndSceneCommand && _sceneOwner == pid && pid != 0)
            {
                _sceneOwner = 0;
                Monitor.PulseAll(_sceneLock);
            }
            return seq;
        }
    }

    public long Submit(GpuCommand cmd)
    {
        cmd.Space ??= AddressSpace.Current; // the submitting thread's process, unless the command names another
        try { Backend?.Prefetch(cmd); } catch (Exception e) { Log.WarnOnce("gpu-prefetch", $"GPU backend prefetch failed: {e}"); }
        return SharedByProcesses ? SubmitArbitrated(cmd) : Enqueue(cmd);
    }

    long Enqueue(GpuCommand cmd)
    {
        cmd.Sequence = Interlocked.Increment(ref _submitted);
        _queue.Enqueue(cmd);
        _available.Release();
        return cmd.Sequence;
    }

    public void WaitFor(long sequence)
    {
        if (Completed >= sequence) return;
        using var blocked = Kernel.Scheduler.Block();
        using var ledger = FrameLedger.Time(FrameLedger.Cat.GuestGpuWait);
        lock (_progress)
            while (Completed < sequence && !_stopping) Monitor.Wait(_progress, 100);
    }

    public void WaitIdle() => WaitFor(Submitted);

    public int Pending => (int)(Submitted - Completed);

    /// <summary>The command the worker is executing (null between commands): hang reports show where the GPU thread is stuck.</summary>
    public GpuCommand? Executing => _executing;
    volatile GpuCommand? _executing;

    void Run()
    {
        NativeThread.Register("GPU worker");
        HostThreads.Apply(HostThreads.Role.Gpu);
        while (!_stopping)
        {
            GpuCommand? cmd;
            try
            {
                if (!_queue.TryDequeue(out cmd))
                {
                    try { lock (_backendLock) Backend?.Idle(); } catch (Exception e) { Log.Error($"GPU backend idle failed: {e}"); }
                    GpuDumpRecorder.Current?.AfterIdle();
                    long ti = FrameLedger.Now();
                    bool got = _available.Wait(50);
                    FrameLedger.AddSince(FrameLedger.Cat.GpuIdle, ti);
                    if (!got || !_queue.TryDequeue(out cmd)) continue;
                }
                else _available.Wait(0);
            }
            catch (ObjectDisposedException) { break; }

            // the command's guest addresses are its process's: the worker reads and writes that address space until the
            // next command (deferred work in Idle runs in the last one; results that outlive it carry their own space)
            AddressSpace.MakeCurrent(cmd.Space);
            if (cmd is CallbackCommand pre)
            {
                // CPU work (transfers) happens first so the backend sees the memory it produced; a transfer that reads a
                // rendered surface needs its pixels in memory first
                if (pre.ReadSize > 0)
                    try { lock (_backendLock) Backend?.BeforeCallback(pre); }
                    catch (Exception e) { Log.WarnOnce("gpu-beforecallback", $"GPU backend failed to prepare a transfer source: {e}"); }
                try { pre.Action(); }
                catch (Exception e) { Log.Error($"GPU callback failed: {e}"); }
            }
            var dump = GpuDumpRecorder.Current; // GPU dump recording (null when off)
            dump?.BeforeExecute(cmd);
            try
            {
                _executing = cmd;
                lock (_backendLock) Backend?.Execute(cmd);
            }
            catch (Exception e)
            {
                Log.WarnOnce("gpu-" + cmd.GetType().Name + e.GetType().Name, $"GPU backend failed on {cmd.GetType().Name}: {e}");
            }
            if (dump != null) lock (_backendLock) dump.AfterExecute(cmd, Backend);

            switch (cmd)
            {
                case DrawCommand draw:
                    draw.ReleaseUniforms(); // executed: its uniform slice is no longer read
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
                _executing = null;
                Interlocked.Exchange(ref _completed, cmd.Sequence);
                Monitor.PulseAll(_progress);
            }
        }
    }

    /// <summary>
    /// Writes a notification in GPU order: after all previously submitted work completed and its results (writebacks,
    /// visibility results) are in guest memory.
    /// </summary>
    public void Notify(uint address, uint value)
    {
        var space = AddressSpace.Current;
        Submit(new SignalCommand { Signal = () => SignalNotification(space, address, value) });
    }

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
