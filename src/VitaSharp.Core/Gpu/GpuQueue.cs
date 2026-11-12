using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using VitaSharp.Core.Kernel;
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
    private readonly ConcurrentQueue<GpuCommand> _queue = new();
    private readonly SemaphoreSlim _available = new(0);
    private readonly Thread _worker;
    private readonly object _progress = new();
    private long _submitted;
    private long _completed;
    private volatile bool _stopping;

    private IGpuBackend? _backend;
    public IGpuBackend? Backend
    {
        get => _backend; set
        {
            _backend = value;
            value?.Attach(this);
        }
    }

    /// <summary>Drains the queue, disposes the current backend and installs a new one (process exec).</summary>
    /// <summary>Held by the worker while it is inside the backend (Execute / Idle); backend replacement takes it too, so a
    /// renderer is never destroyed while the worker is still using it (crashed on LoadExec in windowed mode).</summary>
    private readonly object _backendLock = new();

    /// <summary>Runs a backend swap (the old one's Dispose and the factory) on the thread that owns the presentation
    /// surface and waits for it (desktop: the window thread); null = on the caller.</summary>
    public Action<Action>? ReplaceOn;

    /// <param name="sameTitle">the same title goes on (state load, a new internal resolution or MSAA mode): the new
    /// backend opens the old one's per-title caches (<see cref="IGpuBackend.BeginTitle"/>) once the old one closed them,
    /// so it prewarms the title's shaders and pipelines instead of translating and compiling them all again (every F1
    /// press froze Resistance for 0.6-0.9 s on ~100 shaders and ~50 pipelines). False for LoadExec: the next
    /// executable announces its own title.</param>
    public void ReplaceBackend(Func<IGpuBackend?>? factory, bool sameTitle = false)
    {
        WaitIdle();
        lock (_backendLock)
        {
            void Swap()
            {
                IGpuBackend? old = Backend;
                string? title = sameTitle ? old?.TitleId : null;
                Backend = null;
                old?.Dispose();
                if (factory != null)
                {
                    Backend = factory();
                }

                if (title != null && Backend is { } b)
                {
                    try
                    {
                        b.BeginTitle(title);
                    }
                    catch (Exception e)
                    {
                        Log.Warn($"GPU backend title setup failed: {e.Message}");
                    }
                }
            }
            if (ReplaceOn is { } on)
            {
                on(Swap);
            }
            else
            {
                Swap();
            }
        }
    }

    /// <summary>Experiment hook (VITASHARP_UB_LATE): runs on the worker before a draw executes.</summary>
    public static Action<DrawCommand>? LateUniformCopy;
    /// <summary>
    /// Set by the backend once a program stored to memory (it keeps such ranges in its mirror): from then on the guest
    /// takes a dirty-page sweep when it ends a scene (<see cref="SceneGate.SweepEpoch"/>), so the worker can tell the
    /// guest writes made before a draw's scene ended from the ones made after, while it is still behind.
    /// </summary>
    public static volatile bool SceneEndSweeps;
    /// <summary>GPU worker: the scene-end sweep of the draw being executed (<see cref="SceneGate.ReadBound"/>),
    /// <see cref="uint.MaxValue"/> for other commands or when unknown.</summary>
    public static uint ReadBound { get => t_readBoundPlus1 - 1; set => t_readBoundPlus1 = value + 1; }
    [SuppressMessage("Style", "IDE1006", Justification = "[ThreadStatic]: t_camelCase; .editorconfig cannot select the attribute")]
    [ThreadStatic] private static uint t_readBoundPlus1; // 0 (a thread's default): no bound

    /// <summary>Pulsed whenever a notification value is written (guest waiters re-check).</summary>
    public static readonly object NotificationSync = new();

    /// <summary>How many times the GPU signalled finished work (notification writes), under
    /// <see cref="NotificationSync"/>: the SGX driver's signal counter that sceGpuSignalWait waits on.</summary>
    public static uint SignalCount { get; private set; }
    public object NotificationLock => NotificationSync;

    /// <summary>Writes a notification value in <paramref name="space"/> (the submitting process's) and wakes guest
    /// waiters (used by backends once the GPU really finished).</summary>
    public static void SignalNotification(AddressSpace? space, uint address, uint value)
    {
        lock (NotificationSync)
        {
            using (AddressSpace.Use(space ?? AddressSpace.Current))
            {
                GuestMemory.W32(address, value);
            }

            SignalCount++;
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
    private readonly ConcurrentQueue<DrawCommand> _drawPool = new();
    private int _drawPoolCount;

    public DrawCommand RentDraw()
    {
        if (_drawPool.TryDequeue(out DrawCommand? d))
        {
            Interlocked.Decrement(ref _drawPoolCount);
        }
        else
        {
            d = new DrawCommand();
        }

        d.Reset();
        d.Space = null;
        d.Pid = 0;
        return d;
    }

    public long Completed => Interlocked.Read(ref _completed);
    public long Submitted => Interlocked.Read(ref _submitted);

    // ---- several processes on one GPU: a scene (BeginScene .. EndScene) is one process's at a time. The backend records
    // a scene's draws into the render pass BeginScene opened, so another process's commands must not land in between:
    // while a process has a scene open, the other processes' submissions wait (the GPU is busy with that scene).
    private readonly object _sceneLock = new();
    private int _sceneOwner;

    /// <summary>Set once a second process shares this GPU; until then submissions skip the scene arbitration.</summary>
    public volatile bool SharedByProcesses;

    /// <summary>True while process <paramref name="pid"/> has a scene open (its threads are not parked then: see
    /// <see cref="Kernel.GuestThread"/> suspension).</summary>
    public bool HasOpenScene(int pid) => pid != 0 && (InterleaveScenes ? _openScenes.ContainsKey(pid) : Volatile.Read(ref _sceneOwner) == pid);

    /// <summary>
    /// Processes never wait for each other's scenes (VITASHARP_GPU_SCENE_INTERLEAVE=0: the old arbitration). As on the Vita (each process has
    /// its own GXM context and the GPU interleaves them), every process may have a scene open: the backend records each
    /// process's scene into renderings of its own, suspending the open scene of one process while another's commands
    /// run (<see cref="GpuCommand.Pid"/>). Off: a scene is one process's at a time (the others' submissions wait).
    /// </summary>
    public static readonly bool InterleaveScenes = Environment.GetEnvironmentVariable(EnvVars.GpuSceneInterleave) != "0";

    /// <summary>Processes with a scene open (<see cref="InterleaveScenes"/>).</summary>
    private readonly ConcurrentDictionary<int, byte> _openScenes = new();

    // ---- held draws (InterleaveScenes with a second process): a draw whose uniform buffers are read at its scene's end
    // (SceneGate) is not waited for on the worker, which would hold up every other process's work behind it (the shell
    // keeps its scenes open while it waits for the vblank). The worker puts it, and the process's commands after it,
    // aside in the process's lane and goes on with the other processes' commands; the lane runs once the scene ended, the
    // process waits for one of its commands, or after 250 ms. The renderer keeps each process's scene apart
    // (GpuCommand.Pid), so the order between processes does not matter; within a process it is kept.

    private bool HoldsScenes => InterleaveScenes && SharedByProcesses;

    private sealed class HeldLane
    {
        public readonly Queue<GpuCommand> Commands = new();
        /// <summary>When the draw at the head started waiting.</summary>
        public long Since;
    }

    /// <summary>Worker only: the lanes of processes with held commands.</summary>
    private readonly Dictionary<int, HeldLane> _held = new();
    /// <summary>The sequence of the first held command of each process with a lane (read by waiting threads).</summary>
    private readonly ConcurrentDictionary<int, long> _heldHead = new();
    /// <summary>The highest command sequence a thread of each process waits for; pid 0 = host threads.</summary>
    private readonly ConcurrentDictionary<int, long> _maxWaitedByPid = new();
    /// <summary>The last command the worker took from the queue (run or held).</summary>
    private long _processed;
    private readonly List<int> _drained = new();

    private bool WaitedFor(int pid, long sequence) =>
        _maxWaitedByPid.TryGetValue(pid, out long w) && w >= sequence || _maxWaitedByPid.TryGetValue(0, out long h) && h >= sequence;

    private static void EndGateEarly(SceneGate gate)
    {
        gate.End();
        if (Interlocked.Increment(ref SceneGateReleases) == 1)
        {
            Log.Info("[gpu] a draw of an open scene ran before the scene ended (a guest waited for it, or 250 ms passed): its uniform buffers were read early");
        }
    }

    /// <summary>Worker: runs what the lanes may run now (see above).</summary>
    private void ReleaseHeld()
    {
        long now = System.Diagnostics.Stopwatch.GetTimestamp(), timeout = System.Diagnostics.Stopwatch.Frequency / 4;
        foreach ((int pid, HeldLane? lane) in _held)
        {
            while (lane.Commands.TryPeek(out GpuCommand? gpuCommand))
            {
                if (gpuCommand is DrawCommand { Gate: { Ended: false } g })
                {
                    if (!WaitedFor(pid, gpuCommand.Sequence) && now - lane.Since < timeout && !_stopping)
                    {
                        break;
                    }

                    EndGateEarly(g);
                }
                lane.Commands.Dequeue();
                ExecuteOne(gpuCommand);
                lane.Since = now;
            }
            if (lane.Commands.Count == 0)
            {
                _drained.Add(pid);
            }
        }
        foreach (int pid in _drained)
        {
            _held.Remove(pid);
            _heldHead.TryRemove(pid, out _);
        }
        _drained.Clear();
        Processed(Interlocked.Read(ref _processed));
    }

    /// <summary>Worker: the queue was taken up to <paramref name="sequence"/>; everything before the first held command
    /// is complete.</summary>
    private void Processed(long sequence)
    {
        Interlocked.Exchange(ref _processed, sequence);
        long done = sequence;
        foreach ((int pid, HeldLane? lane) in _held)
        {
            if (lane.Commands.TryPeek(out GpuCommand? gpuCommand))
            {
                _heldHead[pid] = gpuCommand.Sequence;
                if (gpuCommand.Sequence - 1 < done)
                {
                    done = gpuCommand.Sequence - 1;
                }
            }
        }

        if (done > Interlocked.Read(ref _completed))
        {
            Interlocked.Exchange(ref _completed, done); // full fence before reading the waiter count
        }
        else
        {
            Interlocked.MemoryBarrier();
        }

        if (!s_lightSignals || Volatile.Read(ref _progressWaiters) > 0)
        {
            lock (_progress)
            {
                Monitor.PulseAll(_progress);
            }
        }
    }

    /// <summary>A process that ends with a scene open gives the GPU back.</summary>
    public void ReleaseScene(int pid)
    {
        if (InterleaveScenes)
        {
            // the backend drops the scene the process left open (its suspended renderings name surfaces of its memory)
            if (_openScenes.TryRemove(pid, out _))
            {
                Enqueue(new ProcessEndedCommand { Pid = pid, Space = AddressSpace.Current });
            }

            return;
        }
        lock (_sceneLock)
        {
            if (_sceneOwner != pid)
            {
                return;
            }

            _sceneOwner = 0;
            Monitor.PulseAll(_sceneLock);
        }
    }

    /// <summary>Queues <paramref name="cmd"/> once no other process has a scene open (the caller is the current
    /// process; host-side work, pid 0, is never held back) and tracks the scene it opens or closes.</summary>
    private long SubmitArbitrated(GpuCommand cmd)
    {
        int pid = Kernel.ProcessScope.CurrentPid;
        cmd.Pid = pid;
        if (InterleaveScenes)
        {
            if (pid != 0 && cmd is BeginSceneCommand)
            {
                _openScenes[pid] = 0;
            }
            else if (pid != 0 && cmd is EndSceneCommand)
            {
                _openScenes.TryRemove(pid, out _);
            }

            return Enqueue(cmd);
        }
        lock (_sceneLock)
        {
            if (pid != 0 && _sceneOwner != 0 && _sceneOwner != pid)
            {
                using Scheduler.Blocked blocked = Kernel.Scheduler.Block();
                while (_sceneOwner != 0 && _sceneOwner != pid && !_stopping)
                {
                    Monitor.Wait(_sceneLock, 50);
                }
            }
            long seq = Enqueue(cmd);
            if (pid != 0 && cmd is BeginSceneCommand)
            {
                _sceneOwner = pid;
            }
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
        try
        {
            Backend?.Prefetch(cmd);
        }
        catch (Exception e)
        {
            Log.WarnOnce("gpu-prefetch", $"GPU backend prefetch failed: {e}");
        }
        long seq = SharedByProcesses ? SubmitArbitrated(cmd) : Enqueue(cmd);
        TrackSceneThread(cmd);
        return seq;
    }

    // ---- display flips. On the Vita the display controller is not part of the GPU's command stream: the display queue
    // flips to a buffer once the scene that rendered it completed (its fence), and the GPU does not start a later scene
    // into that buffer before the flip away from it (the fragment sync object of sceGxmBeginScene). Here a flip is a
    // PresentCommand the display queue thread submits from its callback, and at the tail of the queue it landed behind
    // whatever the game had submitted since: with the sync object wait on (10/11/2026) that was a later scene into the
    // same buffer, held until this very flip released it, so the flip showed the buffer cleared or half drawn (F1 2011:
    // isolated black frames; Killzone Mercenary: the HUD flickered). A flip taken by the worker before its next command
    // shows the buffer as the scene the fence waited for left it, as on the console.

    /// <summary>VITASHARP_DISPLAY_FLIP_ORDER=queue (A/B): display queue flips go to the tail of the GPU queue as before.</summary>
    private static readonly bool s_flipsAtTail = Environment.GetEnvironmentVariable(EnvVars.DisplayFlipOrder) == "queue";

    /// <summary>Flips submitted with <see cref="SubmitFlip"/>, run by the worker before its next command.</summary>
    private readonly ConcurrentQueue<GpuCommand> _flips = new();

    /// <summary>
    /// Submits a display flip the GXM display queue issues once its frame's fence was reached: the worker runs it before
    /// the next command it takes (and while it holds a scene for a sync object), not after the commands the game queued
    /// since the fence. The caller's earlier submissions are not ordered against it, so only the display queue thread
    /// uses this (a flip from a game thread stays in its queue order).
    /// </summary>
    public void SubmitFlip(PresentCommand cmd)
    {
        if (s_flipsAtTail)
        {
            Submit(cmd);
            return;
        }

        cmd.Space ??= AddressSpace.Current;
        if (SharedByProcesses)
        {
            cmd.Pid = Kernel.ProcessScope.CurrentPid;
        }

        cmd.Sequence = Submitted;
        _flips.Enqueue(cmd);
        // the worker may be asleep on an empty queue: wake it (a leftover count only makes a later wait return early)
        if (!s_lightSignals || Interlocked.CompareExchange(ref _workerSleeping, 0, 1) == 1)
        {
            _available.Release();
        }
    }

    /// <summary>Worker: runs the flips submitted so far (<see cref="SubmitFlip"/>), keeping the address space of the
    /// command it is inside of.</summary>
    private void RunFlips()
    {
        if (_flips.IsEmpty)
        {
            return;
        }

        AddressSpace? space = AddressSpace.Current;
        while (_flips.TryDequeue(out GpuCommand? flip))
        {
            ExecuteOne(flip);
        }

        AddressSpace.MakeCurrent(space);
    }

    /// <summary>Which guest thread has a scene open (it finishes the scene while the emulation is paused, see
    /// <see cref="EmulationPause"/>).</summary>
    private static void TrackSceneThread(GpuCommand cmd)
    {
        if (cmd is not (BeginSceneCommand or EndSceneCommand) || Kernel.GuestThread.Current is not { } t)
        {
            return;
        }

        t.Process.OpenSceneThread = cmd is BeginSceneCommand ? t : null;
    }

    private long Enqueue(GpuCommand cmd)
    {
        cmd.Sequence = Interlocked.Increment(ref _submitted);
        _queue.Enqueue(cmd);
        // wake the worker only when it sleeps: a Release per command took the semaphore's lock on both threads for
        // every draw (the worker also took it back with Wait(0))
        if (!s_lightSignals)
        {
            _available.Release();
        }
        else if (Interlocked.CompareExchange(ref _workerSleeping, 0, 1) == 1)
        {
            _available.Release();
        }

        return cmd.Sequence;
    }

    /// <summary>VITASHARP_GPU_QUEUE_LIGHT (default on): the submit/complete handshake signals only a sleeping side
    /// instead of taking the semaphore's and the progress lock's monitor on every command.</summary>
    private static readonly bool s_lightSignals = Environment.GetEnvironmentVariable(EnvVars.GpuQueueLight) is not ("0" or "off");
    /// <summary>1 while the worker waits for commands (set before its last look at the queue).</summary>
    private int _workerSleeping;
    /// <summary>Threads inside <see cref="WaitFor"/> (the worker pulses the progress monitor only when there are any).</summary>
    private int _progressWaiters;

    /// <summary>VITASHARP_UB_SCENE_END=check: also counts the held draws whose bound uniform buffers changed between the
    /// worker getting the draw and the scene's end (what an early copy would have read wrong).</summary>
    private static readonly bool s_gateCheck = (Environment.GetEnvironmentVariable(EnvVars.UbSceneEnd) ?? "").Contains("check");
    private long _gateHeld, _gateChanged, _gateChangedAfterScene;

    private static uint CopyAndHash(DrawCommand d)
    {
        LateUniformCopy?.Invoke(d);
        return System.IO.Hashing.XxHash32.HashToUInt32(d.VertexUniformSpan) ^ System.IO.Hashing.XxHash32.HashToUInt32(d.FragmentUniformSpan) * 31;
    }

    /// <summary>The highest command sequence a guest thread waits for (<see cref="WaitFor"/>).</summary>
    private long _maxWaited;
    /// <summary>Scene gates given up on (a guest waited for a draw of its still open scene, or the timeout).</summary>
    public static long SceneGateReleases;

    /// <summary>Worker: holds <paramref name="d"/>, whose bound uniform buffers are read late, until the guest ends its
    /// scene. Not when a guest thread waits for this draw or a later command (it would wait for itself), nor longer than
    /// 250 ms.</summary>
    private void WaitSceneGate(DrawCommand d, SceneGate gate)
    {
        long ti = FrameLedger.Now();
        uint before = s_gateCheck ? CopyAndHash(d) : 0, atSceneEnd = before;
        bool ended = false, sceneSeen = gate.SceneEnded;
        for (int i = 0; i < 25 && !_stopping && Interlocked.Read(ref _maxWaited) < d.Sequence; i++)
        {
            // the work recorded so far goes to the GPU and finished frames retire while the draw waits: a sync point
            // before the open scene (the display queue entry of the previous frame, which the guest may wait on before it
            // ends this scene) must still fire
            try
            {
                lock (_backendLock)
                {
                    Backend?.Idle();
                }
            }
            catch (Exception e)
            {
                Log.WarnOnce("gpu-gate-idle", $"GPU backend idle failed during a scene gate wait: {e.Message}");
            }
            if (ended = gate.Wait(10))
            {
                break;
            }

            if (s_gateCheck && !sceneSeen && gate.SceneEnded)
            {
                sceneSeen = true;
                atSceneEnd = CopyAndHash(d);
            }
        }
        FrameLedger.AddSince(FrameLedger.Cat.GpuIdle, ti);
        if (s_gateCheck)
        {
            long held = ++_gateHeld;
            uint after = CopyAndHash(d);
            if (!sceneSeen)
            {
                atSceneEnd = after;
            }

            if (atSceneEnd != before)
            {
                _gateChanged++;
            }

            if (after != atSceneEnd)
            {
                _gateChangedAfterScene++;
            }

            if ((held & (held - 1)) == 0 || held % 20000 == 0)
            {
                Log.Info($"[gpu] scene gate: {held} draws held, {_gateChanged} had uniform buffers rewritten before their scene ended, {_gateChangedAfterScene} between the scene's end and the frame's end (=frame mode only)");
            }
        }
        if (!ended && !gate.Ended)
        {
            gate.End();
            if (Interlocked.Increment(ref SceneGateReleases) == 1)
            {
                Log.Info("[gpu] a draw of an open scene ran before the scene ended (a guest waited for it, or 250 ms passed): its uniform buffers were read early");
            }
        }
    }

    public void WaitFor(long sequence, GuestWait site = GuestWait.Other)
    {
        if (Completed >= sequence)
        {
            return;
        }
        // a guest waiting for a draw of a scene it has not ended: the worker must not hold that draw back
        for (long m = Interlocked.Read(ref _maxWaited); m < sequence; m = Interlocked.Read(ref _maxWaited))
        {
            if (Interlocked.CompareExchange(ref _maxWaited, sequence, m) == m)
            {
                break;
            }
        }

        int pid = HoldsScenes ? Kernel.ProcessScope.CurrentPid : -1;
        if (pid >= 0)
        {
            _maxWaitedByPid.AddOrUpdate(pid, sequence, (_, w) => Math.Max(w, sequence));
        }

        using Scheduler.Blocked blocked = Kernel.Scheduler.Block();
        using FrameLedger.Scope ledger = FrameLedger.Time(FrameLedger.Cat.GuestGpuWait);
        long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        lock (_progress)
        {
            Interlocked.Increment(ref _progressWaiters); // full fence before reading Completed (pairs with the worker's)
            try
            {
                while (!Done(sequence, pid) && !_stopping)
                {
                    Monitor.Wait(_progress, pid >= 0 ? 10 : 100);
                }
            }
            finally
            {
                Interlocked.Decrement(ref _progressWaiters);
            }
        }
        CountGuestWait(site, t0);
    }

    /// <summary>Command <paramref name="sequence"/> and every earlier one complete, as seen by process
    /// <paramref name="pid"/> (-1: the whole queue): another process's held commands do not count.</summary>
    private bool Done(long sequence, int pid) =>
        Completed >= sequence || pid > 0 && Interlocked.Read(ref _processed) >= sequence && !(_heldHead.TryGetValue(pid, out long h) && h <= sequence);

    /// <summary>Where guest threads block on the GPU (PERF guest waits line).</summary>
    public enum GuestWait
    {
        Notification, DisplayQueue, Finish, Throttle, Other
    }

    private static readonly long[] s_guestWaitTicks = new long[5], s_guestWaitCount = new long[5];

    public static void CountGuestWait(GuestWait site, long t0)
    {
        Interlocked.Add(ref s_guestWaitTicks[(int)site], System.Diagnostics.Stopwatch.GetTimestamp() - t0);
        Interlocked.Increment(ref s_guestWaitCount[(int)site]);
    }

    /// <summary>PERF guest waits: blocked guest time per site, in ms per presented frame.</summary>
    public static string GuestWaitReport(long frames)
    {
        frames = Math.Max(1, frames);
        var sb = new System.Text.StringBuilder("PERF guest waits on the GPU (ms/frame, waits/frame):");
        for (int i = 0; i < s_guestWaitTicks.Length; i++)
        {
            sb.Append($" {(GuestWait)i} {Interlocked.Read(ref s_guestWaitTicks[i]) * 1000.0 / System.Diagnostics.Stopwatch.Frequency / frames:F2} ({Interlocked.Read(ref s_guestWaitCount[i]) / (double)frames:F2});");
        }

        return sb.ToString();
    }

    public void WaitIdle() => WaitFor(Submitted);

    public int Pending => (int)(Submitted - Completed);

    /// <summary>The command the worker is executing (null between commands): hang reports show where the GPU thread is stuck.</summary>
    public GpuCommand? Executing => _executing;
    private volatile GpuCommand? _executing;

    private void Run()
    {
        NativeThread.Register("GPU worker", NativeThread.Role.Gpu);
        HostThreads.Apply(HostThreads.Role.Gpu);
        while (!_stopping)
        {
            GpuCommand? cmd;
            RunFlips(); // a flip whose frame is done goes before the work queued after it (see SubmitFlip)
            try
            {
                if (_held.Count > 0)
                {
                    ReleaseHeld();
                }

                if (!_queue.TryDequeue(out cmd))
                {
                    try
                    {
                        lock (_backendLock)
                        {
                            Backend?.Idle();
                        }
                    }
                    catch (Exception e)
                    {
                        Log.Error($"GPU backend idle failed: {e}");
                    }
                    GpuDumpRecorder.Current?.AfterIdle();
                    long ti = FrameLedger.Now();
                    if (s_lightSignals)
                    {
                        // announce the sleep, then look once more: a submit after the flag is set releases the semaphore
                        // (a leftover count only makes a later wait return early)
                        Interlocked.Exchange(ref _workerSleeping, 1);
                        if (_queue.IsEmpty && _flips.IsEmpty)
                        {
                            _available.Wait(_held.Count > 0 ? 2 : 50);
                        }

                        Interlocked.Exchange(ref _workerSleeping, 0);
                        FrameLedger.AddSince(FrameLedger.Cat.GpuIdle, ti);
                        if (!_queue.TryDequeue(out cmd))
                        {
                            continue;
                        }
                    }
                    else
                    {
                        bool got = _available.Wait(_held.Count > 0 ? 2 : 50);
                        FrameLedger.AddSince(FrameLedger.Cat.GpuIdle, ti);
                        if (!got || !_queue.TryDequeue(out cmd))
                        {
                            continue;
                        }
                    }
                }
                else if (!s_lightSignals)
                {
                    _available.Wait(0);
                }
            }
            catch (ObjectDisposedException)
            {
                break;
            }

            RunFlips(); // one submitted while the worker slept or took this command
            if (HoldsScenes && cmd.Pid != 0)
            {
                // the worker holds a process's draws until its scene ends while other processes' work goes on
                if (_held.Count > 0)
                {
                    ReleaseHeld(); // a scene that ended before this command was queued comes first
                }

                if (_held.TryGetValue(cmd.Pid, out HeldLane? lane) || cmd is DrawCommand { Gate.Ended: false } && !WaitedFor(cmd.Pid, cmd.Sequence))
                {
                    if (lane == null)
                    {
                        _held[cmd.Pid] = lane = new HeldLane { Since = System.Diagnostics.Stopwatch.GetTimestamp() };
                    }

                    lane.Commands.Enqueue(cmd);
                    Processed(cmd.Sequence);
                    continue;
                }
            }
            if (HoldsScenes && cmd is DrawCommand { Gate: { Ended: false } waited })
            {
                EndGateEarly(waited); // its process waits for it
            }

            ExecuteOne(cmd);
            Processed(cmd.Sequence);
        }
    }

    /// <summary>Runs one command on the backend (in its process's address space) and retires it.</summary>
    private void ExecuteOne(GpuCommand cmd)
    {
        // the command's guest addresses are its process's: the worker reads and writes that address space until the
        // next command (deferred work in Idle runs in the last one; results that outlive it carry their own space)
        AddressSpace.MakeCurrent(cmd.Space);
        if (cmd is CallbackCommand xfer && (xfer.ReadSize > 0 || xfer.WriteSize > 0))
        {
            try
            {
                lock (_backendLock)
                {
                    xfer.Deferred = Backend?.DeferCallback(xfer) ?? false;
                }
            }
            catch (Exception e)
            {
                Log.WarnOnce("gpu-defercallback", $"GPU backend failed to defer a transfer: {e}");
            }
        }

        if (cmd is CallbackCommand { Deferred: false } pre)
        {
            // CPU work (transfers) happens first so the backend sees the memory it produced; a transfer that reads a
            // rendered surface needs its pixels in memory first
            if (pre.ReadSize > 0)
            {
                try
                {
                    lock (_backendLock)
                    {
                        Backend?.BeforeCallback(pre);
                    }
                }
                catch (Exception e)
                {
                    Log.WarnOnce("gpu-beforecallback", $"GPU backend failed to prepare a transfer source: {e}");
                }
            }

            try
            {
                pre.Action();
            }
            catch (Exception e)
            {
                Log.Error($"GPU callback failed: {e}");
            }
        }
        ReadBound = uint.MaxValue;
        if (cmd is DrawCommand lateDraw)
        {
            if (lateDraw.Gate is { Ended: false } gate)
            {
                WaitSceneGate(lateDraw, gate);
            }

            LateUniformCopy?.Invoke(lateDraw);
            if (lateDraw.Scene is { } scene)
            {
                ReadBound = scene.ReadBound;
            }
        }
        GpuDumpRecorder? dump = GpuDumpRecorder.Current; // GPU dump recording (null when off)
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
            lock (_backendLock)
            {
                Backend?.Execute(cmd);
            }
#if VITASHARP_DEVELOPER
            if (tx != 0)
            {
                SlowDown(tx);
            }

            if (cmd is PresentCommand)
            {
                Util.Developer.DevTrace.GpuPresented();
            }
#endif

        }
        catch (Exception e)
        {
            Log.WarnOnce("gpu-" + cmd.GetType().Name + e.GetType().Name, $"GPU backend failed on {cmd.GetType().Name}: {e}");
        }
        if (dump != null)
        {
            lock (_backendLock)
            {
                dump.AfterExecute(cmd, Backend);
            }
        }

        switch (cmd)
        {
            case DrawCommand draw:
                draw.ReleaseUniforms(); // executed: its uniform slice is no longer read
                if (Volatile.Read(ref _drawPoolCount) < 16384)
                {
                    Interlocked.Increment(ref _drawPoolCount);
                    _drawPool.Enqueue(draw);
                }
                break;
            case SignalCommand signal:
                bool deferred = false;
                // an immediate signal still hands the backend an empty deferred one: the work recorded so far starts
                // on the GPU now (the guest will wait on later sync points for it), not at the next present
                try
                {
                    lock (_backendLock)
                    {
                        deferred = (Backend?.DeferSignal(signal.Immediate ? s_noOp : signal.Signal) ?? false) && !signal.Immediate;
                    }
                }
                catch (Exception e)
                {
                    Log.Error($"GPU backend signal failed: {e}");
                }
                if (!deferred)
                {
                    signal.Signal();
                }

                break;
            case EndSceneCommand end:
                if (end.Notifications.Length > 0 && !end.NotificationsDeferred)
                {
                    lock (NotificationLock)
                    {
                        foreach ((uint addr, uint value) in end.Notifications)
                        {
                            GuestMemory.W32(addr, value);
                        }

                        SignalCount++;
                        Monitor.PulseAll(NotificationLock);
                    }
                }
                break;
        }

        _executing = null;
    }

#if VITASHARP_DEVELOPER
    /// <summary><see cref="EnvVars.GpuWorkerSlowdown"/>: phone-like worker cost on a PC.</summary>
    static readonly double WorkerSlowdown = double.TryParse(Environment.GetEnvironmentVariable(EnvVars.GpuWorkerSlowdown),
        System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var ws) ? ws : 1;

    static void SlowDown(long t0)
    {
        long until = t0 + (long)((System.Diagnostics.Stopwatch.GetTimestamp() - t0) * WorkerSlowdown);
        while (System.Diagnostics.Stopwatch.GetTimestamp() < until)
        {
            Thread.SpinWait(20);
        }
    }
#endif

    /// <summary>
    /// Writes a notification in GPU order: after all previously submitted work completed and its results (writebacks,
    /// visibility results) are in guest memory.
    /// </summary>
    public void Notify(uint address, uint value)
    {
        AddressSpace? space = AddressSpace.Current;
        Submit(new SignalCommand { Signal = () => SignalNotification(space, address, value) });
    }

    /// <summary>A sync point the host can wait on: set once all work submitted before it is complete and published.</summary>
    public sealed class Fence
    {
        private bool _done;
        public bool Done
        {
            get
            {
                lock (this)
                {
                    return _done;
                }
            }
        }
        public void Set()
        {
            lock (this)
            {
                _done = true;
                Monitor.PulseAll(this);
            }
        }
        /// <summary>Waits until set (or <paramref name="keepWaiting"/> turns false).</summary>
        public void Wait(Func<bool> keepWaiting)
        {
            lock (this)
            {
                while (!_done && keepWaiting())
                {
                    Monitor.Wait(this, 50);
                }
            }
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
    /// display queue waits on them before it flips), and the display queue's flips run (<see cref="SubmitFlip"/>: the
    /// flip that releases the buffer a held scene renders into shows that buffer before the scene draws into it).
    /// Returns false on the timeout.
    /// </summary>
    public bool WorkerWait(Func<bool> done, object monitor, int timeoutMs)
    {
        long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        while (true)
        {
            RunFlips();
            try
            {
                lock (_backendLock)
                {
                    Backend?.Idle();
                }
            }
            catch (Exception e)
            {
                Log.WarnOnce("gpu-workerwait", $"GPU backend idle failed during a wait: {e.Message}");
            }
            lock (monitor)
            {
                if (done())
                {
                    return true;
                }

                if (System.Diagnostics.Stopwatch.GetElapsedTime(t0).TotalMilliseconds > timeoutMs)
                {
                    return false;
                }

                Monitor.Wait(monitor, 2);
            }
        }
    }
    private static readonly Action s_noOp = static () => { };

    public void Dispose()
    {
        _stopping = true;
        lock (_progress)
        {
            Monitor.PulseAll(_progress);
        }

        _worker.Join(2000);
        _available.Dispose();
    }
}
