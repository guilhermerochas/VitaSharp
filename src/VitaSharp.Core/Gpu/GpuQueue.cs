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

    /// <summary>Experiment hook (VITASHARP_UB_LATE): runs on the worker before a draw executes.</summary>
    public static Action<DrawCommand>? LateUniformCopy;

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
    // A queue, not a ConcurrentBag: the worker adds and the guest thread takes, so every bag take was a steal (a lock on
    // the worker's list) and every bag Count froze the whole bag (all its locks) once per executed draw.
    readonly ConcurrentQueue<DrawCommand> _drawPool = new();
    int _drawPoolCount;

    public DrawCommand RentDraw()
    {
        if (_drawPool.TryDequeue(out var d)) Interlocked.Decrement(ref _drawPoolCount);
        else d = new DrawCommand();
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
        long seq = SharedByProcesses ? SubmitArbitrated(cmd) : Enqueue(cmd);
        TrackSceneThread(cmd);
        return seq;
    }

    /// <summary>Which guest thread has a scene open (it finishes the scene while the emulation is paused, see
    /// <see cref="EmulationPause"/>).</summary>
    static void TrackSceneThread(GpuCommand cmd)
    {
        if (cmd is not (BeginSceneCommand or EndSceneCommand) || Kernel.GuestThread.Current is not { } t) return;
        t.Process.OpenSceneThread = cmd is BeginSceneCommand ? t : null;
    }

    long Enqueue(GpuCommand cmd)
    {
        cmd.Sequence = Interlocked.Increment(ref _submitted);
        _queue.Enqueue(cmd);
        // wake the worker only when it sleeps: a Release per command took the semaphore's lock on both threads for
        // every draw (the worker also took it back with Wait(0))
        if (!LightSignals) _available.Release();
        else if (Interlocked.CompareExchange(ref _workerSleeping, 0, 1) == 1) _available.Release();
        return cmd.Sequence;
    }

    /// <summary>VITASHARP_GPU_QUEUE_LIGHT (default on): the submit/complete handshake signals only a sleeping side
    /// instead of taking the semaphore's and the progress lock's monitor on every command.</summary>
    static readonly bool LightSignals = Environment.GetEnvironmentVariable(EnvVars.GpuQueueLight) is not ("0" or "off");
    /// <summary>1 while the worker waits for commands (set before its last look at the queue).</summary>
    int _workerSleeping;
    /// <summary>Threads inside <see cref="WaitFor"/> (the worker pulses the progress monitor only when there are any).</summary>
    int _progressWaiters;

    /// <summary>VITASHARP_UB_SCENE_END=check: also counts the held draws whose bound uniform buffers changed between the
    /// worker getting the draw and the scene's end (what an early copy would have read wrong).</summary>
    static readonly bool GateCheck = Environment.GetEnvironmentVariable(EnvVars.UbSceneEnd) == "check";
    long _gateHeld, _gateChanged;

    static uint CopyAndHash(DrawCommand d)
    {
        LateUniformCopy?.Invoke(d);
        return System.IO.Hashing.XxHash32.HashToUInt32(d.VertexUniformSpan) ^ System.IO.Hashing.XxHash32.HashToUInt32(d.FragmentUniformSpan) * 31;
    }

    /// <summary>The highest command sequence a guest thread waits for (<see cref="WaitFor"/>).</summary>
    long _maxWaited;
    /// <summary>Scene gates given up on (a guest waited for a draw of its still open scene, or the timeout).</summary>
    public static long SceneGateReleases;

    /// <summary>Worker: holds <paramref name="d"/>, whose bound uniform buffers are read late, until the guest ends its
    /// scene. Not when a guest thread waits for this draw or a later command (it would wait for itself), nor longer than
    /// 250 ms.</summary>
    void WaitSceneGate(DrawCommand d, SceneGate gate)
    {
        long ti = FrameLedger.Now();
        uint before = GateCheck ? CopyAndHash(d) : 0;
        bool ended = false;
        for (int i = 0; i < 25 && !_stopping && Interlocked.Read(ref _maxWaited) < d.Sequence && !(ended = gate.Wait(10)); i++) { }
        FrameLedger.AddSince(FrameLedger.Cat.GpuIdle, ti);
        if (GateCheck)
        {
            long held = ++_gateHeld;
            if (CopyAndHash(d) != before) _gateChanged++;
            if ((held & (held - 1)) == 0 || held % 20000 == 0)
                Log.Info($"[gpu] scene gate: {held} draws held until their scene ended, {_gateChanged} of them had uniform buffers rewritten meanwhile");
        }
        if (!ended && !gate.Ended)
        {
            gate.End();
            if (Interlocked.Increment(ref SceneGateReleases) == 1)
                Log.Info("[gpu] a draw of an open scene ran before the scene ended (a guest waited for it, or 250 ms passed): its uniform buffers were read early");
        }
    }

    public void WaitFor(long sequence, GuestWait site = GuestWait.Other)
    {
        if (Completed >= sequence) return;
        // a guest waiting for a draw of a scene it has not ended: the worker must not hold that draw back
        for (long m = Interlocked.Read(ref _maxWaited); m < sequence; m = Interlocked.Read(ref _maxWaited))
            if (Interlocked.CompareExchange(ref _maxWaited, sequence, m) == m) break;
        using var blocked = Kernel.Scheduler.Block();
        using var ledger = FrameLedger.Time(FrameLedger.Cat.GuestGpuWait);
        long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        lock (_progress)
        {
            Interlocked.Increment(ref _progressWaiters); // full fence before reading Completed (pairs with the worker's)
            try { while (Completed < sequence && !_stopping) Monitor.Wait(_progress, 100); }
            finally { Interlocked.Decrement(ref _progressWaiters); }
        }
        CountGuestWait(site, t0);
    }

    /// <summary>Where guest threads block on the GPU (PERF guest waits line).</summary>
    public enum GuestWait { Notification, DisplayQueue, Finish, Throttle, Other }

    static readonly long[] GuestWaitTicks = new long[5], GuestWaitCount = new long[5];

    public static void CountGuestWait(GuestWait site, long t0)
    {
        Interlocked.Add(ref GuestWaitTicks[(int)site], System.Diagnostics.Stopwatch.GetTimestamp() - t0);
        Interlocked.Increment(ref GuestWaitCount[(int)site]);
    }

    /// <summary>PERF guest waits: blocked guest time per site, in ms per presented frame.</summary>
    public static string GuestWaitReport(long frames)
    {
        frames = Math.Max(1, frames);
        var sb = new System.Text.StringBuilder("PERF guest waits on the GPU (ms/frame, waits/frame):");
        for (int i = 0; i < GuestWaitTicks.Length; i++)
            sb.Append($" {(GuestWait)i} {Interlocked.Read(ref GuestWaitTicks[i]) * 1000.0 / System.Diagnostics.Stopwatch.Frequency / frames:F2} ({Interlocked.Read(ref GuestWaitCount[i]) / (double)frames:F2});");
        return sb.ToString();
    }

    public void WaitIdle() => WaitFor(Submitted);

    public int Pending => (int)(Submitted - Completed);

    /// <summary>The command the worker is executing (null between commands): hang reports show where the GPU thread is stuck.</summary>
    public GpuCommand? Executing => _executing;
    volatile GpuCommand? _executing;

    void Run()
    {
        NativeThread.Register("GPU worker", NativeThread.Role.Gpu);
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
                    if (LightSignals)
                    {
                        // announce the sleep, then look once more: a submit after the flag is set releases the semaphore
                        // (a leftover count only makes a later wait return early)
                        Interlocked.Exchange(ref _workerSleeping, 1);
                        if (_queue.IsEmpty) _available.Wait(50);
                        Interlocked.Exchange(ref _workerSleeping, 0);
                        FrameLedger.AddSince(FrameLedger.Cat.GpuIdle, ti);
                        if (!_queue.TryDequeue(out cmd)) continue;
                    }
                    else
                    {
                        bool got = _available.Wait(50);
                        FrameLedger.AddSince(FrameLedger.Cat.GpuIdle, ti);
                        if (!got || !_queue.TryDequeue(out cmd)) continue;
                    }
                }
                else if (!LightSignals) _available.Wait(0);
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
            if (cmd is DrawCommand lateDraw)
            {
                if (lateDraw.Gate is { Ended: false } gate) WaitSceneGate(lateDraw, gate);
                LateUniformCopy?.Invoke(lateDraw);
            }
            var dump = GpuDumpRecorder.Current; // GPU dump recording (null when off)
            dump?.BeforeExecute(cmd);
            try
            {
                _executing = cmd;
#if VITASHARP_DEVELOPER
                Util.Developer.DevTrace.GpuCommand(cmd is PresentCommand);
#endif
#if VITASHARP_DEVELOPER
                long tx = WorkerSlowdown > 1 ? System.Diagnostics.Stopwatch.GetTimestamp() : 0;
#endif
                lock (_backendLock) Backend?.Execute(cmd);
#if VITASHARP_DEVELOPER
                if (tx != 0) SlowDown(tx);
                if (cmd is PresentCommand) Util.Developer.DevTrace.GpuPresented();
#endif

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
                    if (Volatile.Read(ref _drawPoolCount) < 16384) { Interlocked.Increment(ref _drawPoolCount); _drawPool.Enqueue(draw); }
                    break;
                case SignalCommand signal:
                    bool deferred = false;
                    // an immediate signal still hands the backend an empty deferred one: the work recorded so far starts
                    // on the GPU now (the guest will wait on later sync points for it), not at the next present
                    try { lock (_backendLock) deferred = (Backend?.DeferSignal(signal.Immediate ? NoOp : signal.Signal) ?? false) && !signal.Immediate; }
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

            _executing = null;
            Interlocked.Exchange(ref _completed, cmd.Sequence); // full fence before reading the waiter count
            if (!LightSignals || Volatile.Read(ref _progressWaiters) > 0)
                lock (_progress) Monitor.PulseAll(_progress);
        }
    }

#if VITASHARP_DEVELOPER
    /// <summary><see cref="EnvVars.GpuWorkerSlowdown"/>: phone-like worker cost on a PC.</summary>
    static readonly double WorkerSlowdown = double.TryParse(Environment.GetEnvironmentVariable(EnvVars.GpuWorkerSlowdown),
        System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var ws) ? ws : 1;

    static void SlowDown(long t0)
    {
        long until = t0 + (long)((System.Diagnostics.Stopwatch.GetTimestamp() - t0) * WorkerSlowdown);
        while (System.Diagnostics.Stopwatch.GetTimestamp() < until) Thread.SpinWait(20);
    }
#endif

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

    /// <param name="immediate">set once the worker reached it (<see cref="SignalCommand.Immediate"/>), not once the GPU
    /// finished the work before it</param>
    public Fence SubmitFence(bool immediate = false)
    {
        var f = new Fence();
        Submit(new SignalCommand { Signal = f.Set, Immediate = immediate });
        return f;
    }

    /// <summary>
    /// GPU worker only (inside a <see cref="CallbackCommand"/>): holds the GPU until <paramref name="done"/> (checked
    /// under <paramref name="monitor"/>, which the releasing thread pulses) or the timeout. The work recorded so far is
    /// submitted and finished frames keep retiring meanwhile, so sync points queued before this one still fire (the
    /// display queue waits on them before it flips). Returns false on the timeout.
    /// </summary>
    public bool WorkerWait(Func<bool> done, object monitor, int timeoutMs)
    {
        long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        while (true)
        {
            try { lock (_backendLock) Backend?.Idle(); }
            catch (Exception e) { Log.WarnOnce("gpu-workerwait", $"GPU backend idle failed during a wait: {e.Message}"); }
            lock (monitor)
            {
                if (done()) return true;
                if (System.Diagnostics.Stopwatch.GetElapsedTime(t0).TotalMilliseconds > timeoutMs) return false;
                Monitor.Wait(monitor, 2);
            }
        }
    }
    static readonly Action NoOp = static () => { };

    public void Dispose()
    {
        _stopping = true;
        lock (_progress) Monitor.PulseAll(_progress);
        _worker.Join(2000);
        _available.Dispose();
    }
}
