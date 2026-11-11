using System.Buffers;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.IO.Compression;
using System.Text;
using VitaSharp.Core.Gpu;
using VitaSharp.Core.SaveState;
using VitaSharp.Core.Util;

namespace VitaSharp.Core.Capture;

/// <summary>What a front-end gives the session capture (desktop Runner, Android EmulationSession).</summary>
public sealed class SessionCaptureOptions
{
    /// <summary>Folder the captures go to (&lt;data&gt;/captures).</summary>
    public required string Root;
    /// <summary>"desktop" or "android" (session.txt; the analyzer's re-run hint).</summary>
    public string Platform = "desktop";
    /// <summary>Header of the GPU dump (title, build, GPU, renderer settings).</summary>
    public Func<GpuDumpHeader>? DumpHeader;
    /// <summary>Guest threads, mutexes and GPU queue (threads_start.txt / threads_stop.txt).</summary>
    public Func<string>? Threads;
    /// <summary>Extra session.txt lines: build, renderer, window, settings.</summary>
    public Func<string>? Info;
    /// <summary>The session log file (copied to log.txt at the stop).</summary>
    public Func<string?>? LogFile;
    /// <summary>Installs (or with null removes) the renderer's present tap (VulkanRendererOptions.PresentTap).</summary>
    public required Action<IPresentTap?> SetPresentTap;
    /// <summary>Short user feedback (title bar, toast), Portuguese.</summary>
    public Action<string>? Status;
    /// <summary>Recording state changed (on any thread): the REC badge.</summary>
    public Action<bool>? RecordingChanged;
    /// <summary>The run's options (session.txt "options:"), reused by the re-run.</summary>
    public string[] RunOptions = [];
    /// <summary>GPU dump frame limit of one recording (the dump stops by itself; the rest keeps recording).</summary>
    public long DumpMaxFrames = 5400;
    /// <summary>Memory limit of the pre-roll's encoded frames.</summary>
    public long PreRollMaxBytes = 192L << 20;
    /// <summary>Seconds between the pre-roll's save states (the game stops ~0.1-0.3 s for each; two are kept, so the
    /// pre-roll reaches one to two intervals back). At least the pre-roll time.</summary>
    public double PreRollStateInterval = 30;
    /// <summary>Presented pictures waiting for the encoder before new ones are dropped (their timing is kept).</summary>
    public int EncoderQueueFrames = 120;
    /// <summary>Recording parts (a re-run records without a state or a GPU dump).</summary>
    public bool StartState = true, GpuDump = true, Zip = true;
    /// <summary>Longest recording: it stops and saves by itself after this many seconds (0 = no limit). A 6-minute
    /// recording (PCSE00333, 14,603 frames) was 2.6 GB and took over two minutes to save.
    /// VITASHARP_CAPTURE_MAX_SECONDS / --capture-max=SECONDS.</summary>
    public double MaxSeconds = DefaultMaxSeconds;
    /// <summary>Largest recording: it stops by itself once its encoded pictures (frames.vsfr) pass this many bytes
    /// (0 = no limit).</summary>
    public long MaxBytes = 1L << 30;
    public const double DefaultMaxSeconds = 60;
    /// <summary>Hotkey name in the messages ("F8"; Android: the pause menu).</summary>
    public string StopHint = Localization.Loc.Get("CaptureStopHint");
}

/// <summary>
/// "Gravar tudo": one recording, started and stopped by the player, with everything needed to diagnose a problem
/// offline (docs/notes/session-capture.md): a save state at the start, every input the guest read, the GPU dump,
/// every presented frame as shown (QOI, with present/flip/vblank timing), per-frame timing and stall causes, the
/// guest audio (ports and NGS voices, plus a mix), the log, the HLE error summary and the thread states. With the
/// pre-roll armed, the last seconds before the start (frames, timing, input and a periodic save state) are kept in
/// memory and included, so a recording started right after a glitch still contains it.
/// <para>Off (neither armed nor recording) nothing runs: no tap, no input wrapper, no thread.</para>
/// </summary>
public sealed class SessionCapture : IPresentTap, IRandomHook, IDisposable
{
    readonly Emulator _emu;
    readonly SessionCaptureOptions _o;
    readonly object _lock = new();
    InputRecorder? _input;
    BlockingCollection<Work>? _queue;
    Thread? _encoder;
    int _busy;
    Recording? _rec;
    bool _disposed;

    // pre-roll
    double _preRollSeconds;
    Thread? _stateThread;
    readonly AutoResetEvent _stateWake = new(false);
    readonly SemaphoreSlim _stateGate = new(1, 1);
    readonly LinkedList<(PresentInfo Info, byte[] Data, byte Flags)> _ringFrames = new();
    long _ringBytes;
    readonly LinkedList<InputEvent> _ringInput = new();
    readonly List<PreRollState> _states = new();
    int _stateSeq;
    bool _statesRefused;
    long _droppedPictures;
    // timing of the pre-roll's periodic states (game stopped) for the PERF line
    readonly List<double> _stateStopMs = new();

    double StateInterval => Math.Max(_preRollSeconds, _o.PreRollStateInterval);

    sealed record PreRollState(string Path, long StartNs, long GuestUs, long Flip);

    enum WorkKind
    {
        Frame, FlushPreRoll, Close
    }

    readonly record struct Work(WorkKind Kind, PresentInfo Info, byte[]? Pixels, int W, int H, Recording? Rec, ManualResetEventSlim? Done);

    sealed class Recording(string dir)
    {
        public readonly string Dir = dir;
        public readonly List<InputEvent> Input = new();
        public readonly List<(PresentInfo Info, byte Flags, int Bytes)> Frames = new();
        public readonly List<string> Notes = new();
        public FileStream? FramesFile;
        public long FrameBytes;
        public long StartNs, StopNs, StartFlip, StopFlip, StartUs, StopUs;
        public DateTime StartTime, StopTime;
        public PreRollState? PreRoll;
        public (long GuestUs, long Ns, long Flip)? StartState;
        public bool OwnsAudio;
        public GpuDumpRecorder? Dump;
        public GpuDumpResult? DumpResult;
        public readonly ManualResetEventSlim DumpDone = new(true);
        public long RandomCalls;
        public volatile bool LimitReached;
        public double StartStateMs;
        public long ProcessCpuMs0;
        /// <summary>The last pad/touch changes before the start (input_before.tsv); the last "where" line written.</summary>
        public List<InputEvent> Before = new();
        public string LastWhere = "";
        public long PresentsSeen;
    }

    string SafeWhere()
    {
        try
        {
            return CaptureContext.Where(_emu);
        }
        catch (Exception e)
        {
            return $"(unknown: {e.GetType().Name})";
        }
    }

    string SafeDescribe(string when)
    {
        try
        {
            return CaptureContext.Describe(_emu, when);
        }
        catch (Exception e)
        {
            return $"[{when}]\n(context unavailable: {e.Message})\n";
        }
    }

    public SessionCapture(Emulator emu, SessionCaptureOptions options)
    {
        _emu = emu;
        _o = options;
        // the last button and touch changes are always kept (a compare per poll): the report says what the player did
        // just before pressing F8 even without the pre-roll
        lock (_lock)
        {
            InstallInput();
            _input!.RecordMotion = false;
        }
    }

    /// <summary>The last pad and touch changes while no recording runs (input_before.tsv of the next recording).</summary>
    readonly LinkedList<InputEvent> _recent = new();
    const int RecentInputs = 32;

    public SessionCaptureOptions Options => _o;
    public bool IsRecording => _rec != null;
    public bool Armed => _preRollSeconds > 0;
    public double PreRollSeconds => _preRollSeconds;
    /// <summary>The folder of the running recording, or of the last one.</summary>
    public string? Dir { get; private set; }
    /// <summary>The zip of the last completed recording.</summary>
    public string? LastZip { get; private set; }
    /// <summary>Set when the last recording was completely written (tests, scripted runs wait on it).</summary>
    public readonly ManualResetEventSlim Idle = new(true);
    /// <summary>Seconds since the running recording started (0 when none runs): the REC badge.</summary>
    public double RecordingSeconds => _rec is { } r ? (CaptureClock.NowNs - r.StartNs) / 1e9 : 0;
    /// <summary>Progress of the save in percent (0-100), -1 when nothing is being saved: the "saving NN%" badge.</summary>
    public int SavePercent => Volatile.Read(ref _savePercent);
    /// <summary>What the save is doing now ("frames", "analysis", "zip"...), null when nothing is being saved.</summary>
    public string? SaveStep => Volatile.Read(ref _saveStep);
    int _savePercent = -1;
    string? _saveStep;
    string? _stopPending;
    volatile bool _starting;
    readonly CancellationTokenSource _hurry = new();

    /// <summary>The window is closing while a recording is saved: the optional steps (the analysis, report.txt) are
    /// skipped so the close waits only for the files themselves. Any thread.</summary>
    public void Hurry()
    {
        if (_hurry.IsCancellationRequested)
        {
            return;
        }

        Log.Info("[capture] hurry: the window is closing, the analysis (report.txt) is skipped");
        _hurry.Cancel();
    }

    void SetProgress(string step, double pct)
    {
        Volatile.Write(ref _saveStep, step);
        Volatile.Write(ref _savePercent, (int)Math.Clamp(pct, 0, 99));
    }

    // ------------------------------------------------------------------ arming

    /// <summary>Keeps the last <paramref name="seconds"/> (frames, timing, input, a save state every
    /// <paramref name="seconds"/>) in memory; 0 disarms.</summary>
    public void SetPreRoll(double seconds)
    {
        lock (_lock)
        {
            if (_disposed)
            {
                return;
            }

            seconds = Math.Clamp(seconds, 0, 60);
            if (seconds == _preRollSeconds)
            {
                return;
            }

            _preRollSeconds = seconds;
            if (seconds > 0)
            {
                EnsureRunning();
                if (_stateThread == null)
                {
                    _stateThread = new Thread(PreRollStateLoop) { IsBackground = true, Name = "capture pre-roll", Priority = ThreadPriority.BelowNormal };
                    _stateThread.Start();
                }
                Log.Info($"[capture] pre-roll on: frames, timing and input since the older of the last two save states (one every {StateInterval:F0} s; at least {seconds:F0} s) kept for Gravar tudo");
            }
            else
            {
                ClearRing();
                Log.Info("[capture] pre-roll off");
                if (_rec == null)
                {
                    StopRunning();
                }
            }
        }
        _stateWake.Set();
    }

    /// <summary>The tap, the input wrapper and the encoder thread: on while armed or recording.</summary>
    void EnsureRunning()
    {
        if (_queue == null)
        {
            _queue = new BlockingCollection<Work>(new ConcurrentQueue<Work>());
            _encoder = new Thread(EncoderLoop) { IsBackground = true, Name = "capture encoder" };
            _encoder.Start();
        }
        InstallInput();
        _input!.RecordMotion = true;
        _o.SetPresentTap(this);
    }

    void StopRunning()
    {
        _o.SetPresentTap(null);
        if (_disposed)
        {
            UninstallInput();
        }
        else
        {
            _input?.RecordMotion = false;
        }

        _queue?.CompleteAdding();
        _queue = null;
        _encoder = null;
    }

    void InstallInput()
    {
        var host = _emu.Host;
        if (host.Input is InputRecorder && host.Motion is InputRecorder)
        {
            return;
        }

        var inner = host.Input is InputRecorder r ? r.Inner : host.Input;
        var innerMotion = host.Motion is InputRecorder rm ? rm.InnerMotion : host.Motion;
        _input = new InputRecorder(inner, innerMotion, AddInput);
        host.Input = _input;
        host.Motion = _input;
    }

    void UninstallInput()
    {
        var host = _emu.Host;
        if (host.Input is InputRecorder r && ReferenceEquals(r, _input))
        {
            host.Input = r.Inner;
        }

        if (host.Motion is InputRecorder m && ReferenceEquals(m, _input))
        {
            host.Motion = m.InnerMotion;
        }

        _input = null;
    }

    void ClearRing()
    {
        _ringFrames.Clear();
        _ringBytes = 0;
        _ringInput.Clear();
        foreach (var s in _states)
        {
            TryDelete(s.Path);
        }

        _states.Clear();
    }

    string PreRollDir => Path.Combine(_o.Root, ".preroll");

    /// <summary>A save state every pre-roll period while armed and not recording (the oldest two are kept, so the ring
    /// always starts at a state at least one period back).</summary>
    void PreRollStateLoop()
    {
        while (true)
        {
            double secs;
            lock (_lock)
            {
                if (_disposed || _preRollSeconds <= 0)
                {
                    _stateThread = null;
                    return;
                }
                secs = _preRollSeconds;
            }
            _stateWake.WaitOne(TimeSpan.FromSeconds(_states.Count == 0 ? 2 : StateInterval));
            lock (_lock)
            {
                if (_disposed || _preRollSeconds <= 0)
                {
                    _stateThread = null;
                    return;
                }
            }

            if (_rec != null || _statesRefused || EmulationPause.IsPaused || !_emu.Running || _emu.MainModule == null)
            {
                continue;
            }

            if (!_stateGate.Wait(0))
            {
                continue;
            }

            try
            {
                Directory.CreateDirectory(PreRollDir);
                string path = Path.Combine(PreRollDir, $"{Safe(_emu.TitleId)}_{++_stateSeq % 4}.vss");
                long ns = CaptureClock.NowNs, flip = Hle.Modules.Display.FramesPresented;
                var sw = Stopwatch.StartNew();
                AddInput(InputEvent.Now("mark", "preroll_state begin"));
                var header = SaveStates.Save(_emu, path, null, 2000);
                lock (_stateStopMs)
                {
                    _stateStopMs.Add(sw.Elapsed.TotalMilliseconds);
                }

                AddInput(InputEvent.Now("mark", "preroll_state"));
                lock (_lock)
                {
                    _states.Add(new PreRollState(path, ns, (long)(header.GuestSeconds * 1e6), flip));
                    while (_states.Count > 2)
                    {
                        TryDelete(_states[0].Path);
                        _states.RemoveAt(0);
                    }
                    TrimRing();
                }
            }
            catch (SnapshotException e)
            {
                // refused for the whole session (shell, unsupported module): the pre-roll keeps frames and input
                if (e.Message.Contains("not supported", StringComparison.Ordinal) || e.Message.Contains("shell", StringComparison.Ordinal))
                {
                    _statesRefused = true;
                    Log.Warn($"[capture] pre-roll: no save states in this session ({e.Message}); frames, timing and input are still kept");
                }
                else
                {
                    Log.Info($"[capture] pre-roll save state skipped: {e.Message}");
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                Log.Warn($"[capture] pre-roll save state: {e.Message}");
            }
            finally
            {
                _stateGate.Release();
            }
        }
    }

    /// <summary>Drops pre-roll items older than the oldest kept state (or than twice the period without one), and the
    /// oldest frames over the memory limit. Under <see cref="_lock"/>.</summary>
    void TrimRing()
    {
        long now = CaptureClock.NowNs;
        long keepFrom = Math.Min(_states.Count > 0 ? _states[0].StartNs : long.MaxValue, now - (long)(1e9 * Math.Max(_preRollSeconds, _states.Count > 0 ? 0 : 2 * StateInterval)));
        while (_ringFrames.First is { } f && (f.Value.Info.PresentNs < keepFrom || _ringBytes > _o.PreRollMaxBytes))
        {
            _ringBytes -= f.Value.Data.Length;
            _ringFrames.RemoveFirst();
        }
        // a delta needs the pictures before it: the ring starts at a key picture
        while (_ringFrames.First is { } d && (d.Value.Flags & FramesFile.FlagDelta) != 0)
        {
            _ringBytes -= d.Value.Data.Length;
            _ringFrames.RemoveFirst();
        }
        while (_ringInput.First is { } i && i.Value.Ns < keepFrom)
        {
            _ringInput.RemoveFirst();
        }
    }

    // ------------------------------------------------------------------ data in

    void AddInput(InputEvent e)
    {
        lock (_lock)
        {
            if (_rec is { } r)
            {
                r.Input.Add(e);
                return;
            }
            if (e.Kind is "pad" or "touch")
            {
                _recent.AddLast(e);
                if (_recent.Count > RecentInputs)
                {
                    _recent.RemoveFirst();
                }
            }
            if (_preRollSeconds > 0)
            {
                _ringInput.AddLast(e);
                if (_ringInput.Count % 256 == 0)
                {
                    TrimRing();
                }
            }
        }
    }

    /// <summary>An emulator hotkey or another front-end event, in input.tsv (kind key).</summary>
    public void NoteKey(string name)
    {
        if (_rec != null || _preRollSeconds > 0)
        {
            AddInput(InputEvent.Now("key", name));
        }
    }

    public void OnPresent(in PresentInfo info, ReadOnlySpan<byte> rgba, int w, int h)
    {
        var q = _queue;
        if (q == null)
        {
            return;
        }

        Recording? dest;
        lock (_lock)
        {
            dest = _rec;
        }

        if (dest == null && _preRollSeconds <= 0)
        {
            return;
        }

        if (dest != null && !dest.LimitReached)
        {
            CheckLimit(dest);
        }
        // screen / application changes during the recording (input.tsv kind ctx): checked every 15th present
        if (dest != null && ++dest.PresentsSeen % 15 == 0)
        {
            string where = SafeWhere();
            if (where != dest.LastWhere)
            {
                dest.LastWhere = where;
                AddInput(InputEvent.Now("ctx", where));
            }
        }
        byte[]? px = null;
        // the encoder fell behind (slow device): keep the timing, drop the picture
        if (q.Count < _o.EncoderQueueFrames && rgba.Length >= w * h * 4)
        {
            px = ArrayPool<byte>.Shared.Rent(w * h * 4);
            rgba[..(w * h * 4)].CopyTo(px);
        }
        else
        {
            Interlocked.Increment(ref _droppedPictures);
        }

        try
        {
            q.Add(new Work(WorkKind.Frame, info, px, w, h, dest, null));
        }
        catch (InvalidOperationException)
        {
            if (px != null)
            {
                ArrayPool<byte>.Shared.Return(px);
            }
        }
    }

    /// <summary>The length / size limit: the recording stops and saves by itself, with a notice (GPU worker: only the
    /// check runs here, the stop on a pool thread).</summary>
    void CheckLimit(Recording r)
    {
        double secs = (CaptureClock.NowNs - r.StartNs) / 1e9;
        bool time = _o.MaxSeconds > 0 && secs >= _o.MaxSeconds;
        bool size = _o.MaxBytes > 0 && Interlocked.Read(ref r.FrameBytes) >= _o.MaxBytes;
        if (!time && !size)
        {
            return;
        }

        r.LimitReached = true;
        string why = time ? FormattableString.Invariant($"{_o.MaxSeconds:F0} s") : $"{_o.MaxBytes >> 20} MB";
        Log.Info($"[capture] limit reached ({why}): the recording stops and saves by itself");
        lock (r.Notes)
        {
            r.Notes.Add($"stopped by the limit ({why}; VITASHARP_CAPTURE_MAX_SECONDS / --capture-max=SECONDS change it, 0 = none)");
        }

        _o.Status?.Invoke(Localization.Loc.Format("CaptureLimit", why));
        Task.Run(() =>
        {
            for (int i = 0; i < 400 && _rec == r && !Stop("limit"); i++)
            {
                Thread.Sleep(25);
            }
        });
    }

    bool IRandomHook.Replay(Span<byte> dst) => false;

    void IRandomHook.Recorded(ReadOnlySpan<byte> bytes)
    {
        if (_rec is not { } r)
        {
            return;
        }

        Interlocked.Increment(ref r.RandomCalls);
        AddInput(InputEvent.Now("rng", Convert.ToHexString(bytes)));
    }

    void EncoderLoop()
    {
        var q = _queue!;
        var enc = new FramesFile.Encoder();
        foreach (var work in q.GetConsumingEnumerable())
        {
            try
            {
                switch (work.Kind)
                {
                    case WorkKind.Frame:
                        byte[] data = [];
                        byte flags = 0;
                        if (work.Pixels != null)
                        {
                            (data, flags) = enc.Encode(work.Pixels.AsSpan(0, work.W * work.H * 4), work.W, work.H);
                            ArrayPool<byte>.Shared.Return(work.Pixels);
                        }
                        if (work.Rec is { } r)
                        {
                            WriteFrame(r, work.Info, flags, data);
                        }
                        else
                        {
                            lock (_lock)
                            {
                                if (_preRollSeconds <= 0)
                                {
                                    enc.ForceKey();
                                    break;
                                }
                                _ringFrames.AddLast((work.Info, data, flags));
                                _ringBytes += data.Length;
                                TrimRing();
                            }
                        }

                        break;
                    case WorkKind.FlushPreRoll:
                    {
                        List<(PresentInfo Info, byte[] Data, byte Flags)> ring;
                        lock (_lock)
                        {
                            ring = _ringFrames.ToList();
                            _ringFrames.Clear();
                            _ringBytes = 0;
                        }
                        // the ring starts at a key picture and ends with the encoder's last picture: the recording
                        // continues its chain; without a ring it starts with a key picture
                        if (ring.Count == 0)
                        {
                            enc.ForceKey();
                        }

                        foreach (var (info, bytes, fl) in ring)
                        {
                            WriteFrame(work.Rec!, info, (byte)(fl | FramesFile.FlagPreRoll), bytes);
                        }

                        break;
                    }
                    case WorkKind.Close:
                        work.Rec!.FramesFile?.Dispose();
                        work.Rec.FramesFile = null;
                        enc.ForceKey();
                        work.Done?.Set();
                        break;
                }
            }
            catch (Exception e)
            {
                Log.WarnOnce("capture-encoder", $"[capture] frame encoder: {e.Message}");
                enc.ForceKey();
                work.Done?.Set();
            }
        }
    }

    static void WriteFrame(Recording r, in PresentInfo info, byte flags, byte[] data)
    {
        if (r.FramesFile == null)
        {
            return;
        }

        if (data.Length == 0)
        {
            flags |= FramesFile.FlagNoPicture;
        }

        FramesFile.WriteRecord(r.FramesFile, info, flags, data);
        r.FrameBytes += data.Length;
        lock (r.Frames)
        {
            r.Frames.Add((info, flags, data.Length));
        }
    }

    // ------------------------------------------------------------------ start / stop

    /// <summary>F8 / the pause menu: starts a recording, or stops the running one. Never blocks.</summary>
    public void Toggle(string reason = "hotkey")
    {
        // every press is logged with what it did: a press that seems lost can be told apart from one that was refused
        bool stop = _rec != null || _starting;
        bool ok = stop ? Stop(reason) : Start(reason);
        Log.Info($"[capture] {reason} pressed: {(stop ? "stop" : "start")} {(ok ? "accepted" : SavePercent >= 0 ? $"refused (the previous recording is still being saved, {SavePercent}%)" : "refused")}");
    }

    /// <summary>Starts a recording (in the background: the start save state stops the game for ~0.1-0.3 s).
    /// <paramref name="limit"/>: whether the length / size limit applies (a scripted --capture-at range or a re-run
    /// records exactly what it asked for).</summary>
    public bool Start(string reason = "hotkey", bool limit = true)
    {
        if (Interlocked.CompareExchange(ref _busy, 1, 0) != 0)
        {
            _o.Status?.Invoke(Localization.Loc.Get("CaptureWait"));
            return false;
        }
        if (_rec != null)
        {
            Volatile.Write(ref _busy, 0);
            return false;
        }
        Idle.Reset();
        _starting = true;
        Task.Run(() =>
        {
            try
            {
                StartCore(reason, limit);
            }
            catch (Exception e)
            {
                Log.Warn($"[capture] start failed: {e}");
                _o.Status?.Invoke(Localization.Loc.Format("CaptureStartFailed", e.Message));
                Idle.Set();
            }
            finally
            {
                _starting = false;
                Volatile.Write(ref _busy, 0);
                // a stop pressed while the recording was starting (start save state): done now
                if (Interlocked.Exchange(ref _stopPending, null) is { } why && _rec != null)
                {
                    Stop(why);
                }
            }
        });
        return true;
    }

    void StartCore(string reason, bool limit)
    {
        string title = Safe(string.IsNullOrEmpty(_emu.TitleId) ? "NOTITLE" : _emu.TitleId);
        string baseName = $"{title}_{DateTime.Now:yyyyMMdd_HHmmss}_tudo";
        string dir = Path.Combine(_o.Root, baseName);
        for (int i = 2; Directory.Exists(dir) || File.Exists(dir + ".zip"); i++)
        {
            dir = Path.Combine(_o.Root, $"{baseName}_{i}");
        }

        Directory.CreateDirectory(dir);
        var r = new Recording(dir)
        {
            LimitReached = !limit,
            StartNs = CaptureClock.NowNs,
            StartFlip = Hle.Modules.Display.FramesPresented,
            StartUs = GuestClock.Microseconds,
            StartTime = DateTime.Now,
            ProcessCpuMs0 = (long)Process.GetCurrentProcess().TotalProcessorTime.TotalMilliseconds,
        };
        r.FramesFile = new FileStream(Path.Combine(dir, "frames.vsfr"), FileMode.Create, FileAccess.Write, FileShare.Read, 1 << 20);
        FramesFile.WriteHeader(r.FramesFile);
        Dir = dir;
        PreRollState? pre;
        string where = SafeWhere();
        lock (_lock)
        {
            EnsureRunning();
            pre = _states.Count > 0 ? _states[0] : null;
            r.PreRoll = pre;
            long from = pre?.StartNs ?? long.MinValue;
            foreach (var e in _ringInput)
            {
                if (e.Ns >= from)
                {
                    r.Input.Add(e);
                }
            }

            _ringInput.Clear();
            r.Before = _recent.ToList();
            _recent.Clear();
            r.Input.Add(InputEvent.Now("mark", $"start {reason}"));
            r.Input.Add(InputEvent.Now("ctx", where));
            r.LastWhere = where;
            _rec = r;
            _queue!.Add(new Work(WorkKind.FlushPreRoll, default, null, 0, 0, r, null));
        }
        Log.Info($"[capture] recording ({reason}) at guest frame {r.StartFlip}: {dir}" + (pre != null ? $"; pre-roll from guest frame {pre.Flip} ({(r.StartNs - pre.StartNs) / 1e9:F1} s before)" : _preRollSeconds > 0 ? "; pre-roll without a save state yet" : ""));
        _o.RecordingChanged?.Invoke(true);
        _o.Status?.Invoke(Localization.Loc.Format("CaptureRecording", _o.StopHint));
        if (pre != null)
        {
            try
            {
                File.Copy(pre.Path, Path.Combine(dir, "state_preroll.vss"));
            }
            catch (IOException e)
            {
                r.Notes.Add($"pre-roll state not copied: {e.Message}");
                r.PreRoll = null;
            }
        }
        if (!AudioDump.Enabled)
        {
            AudioDump.Enable(Path.Combine(dir, "audio"));
            AudioDump.VoiceDumps = true;
            r.OwnsAudio = true;
        }
        else
        {
            r.Notes.Add("audio: --audio-dump / VITASHARP_AUDIO_DUMP was already recording the ports elsewhere; no audio/ in this capture");
        }

        DebugMode.CollectHleErrors = true;
        CaptureHooks.Random ??= this;
        WriteText(dir, "threads_start.txt", _o.Threads);
        WriteText(dir, "context.txt", () => SafeDescribe("start of the recording"));

        if (_o.StartState)
        {
            _stateGate.Wait(5000);
            try
            {
                var sw = Stopwatch.StartNew();
                long ns = CaptureClock.NowNs, flip = Hle.Modules.Display.FramesPresented;
                lock (_lock)
                {
                    r.Input.Add(InputEvent.Now("mark", "state_start begin"));
                }

                var h = SaveStates.Save(_emu, Path.Combine(dir, "state_start.vss"), null, 3000);
                r.StartStateMs = sw.Elapsed.TotalMilliseconds;
                r.StartState = ((long)(h.GuestSeconds * 1e6), ns, flip);
                lock (_lock)
                {
                    r.Input.Add(InputEvent.Now("mark", "state_start"));
                }
            }
            catch (SnapshotException e)
            {
                r.Notes.Add($"no start save state: {e.Message}");
                Log.Info($"[capture] start save state: {e.Message}");
            }
            finally
            {
                _stateGate.Release();
            }
        }

        if (_o.GpuDump && _rec == r)
        {
            r.DumpDone.Reset();
            r.Dump = GpuDumpRecorder.Start(new GpuDumpOptions
            {
                Path = Path.Combine(dir, "gpu" + GpuDumpFormat.Extension),
                Header = _o.DumpHeader?.Invoke() ?? new GpuDumpHeader(),
                MaxFrames = _o.DumpMaxFrames,
                Finished = res => { r.DumpResult = res; r.DumpDone.Set(); },
            });
            if (r.Dump == null)
            {
                r.DumpDone.Set();
                r.Notes.Add("no GPU dump: another GPU dump (Ctrl+F8) was recording");
            }
        }
    }

    /// <summary>Stops the recording and writes the folder and its zip in the background.</summary>
    public bool Stop(string reason = "hotkey")
    {
        if (Interlocked.CompareExchange(ref _busy, 1, 0) != 0)
        {
            // still starting (the start save state): the stop is kept and done as soon as the start completes, so a
            // press is never lost
            if (_starting)
            {
                Volatile.Write(ref _stopPending, reason);
                _o.Status?.Invoke(Localization.Loc.Get("CaptureStarting"));
                return true;
            }
            return false;
        }
        if (_rec == null)
        {
            Volatile.Write(ref _busy, 0);
            return false;
        }
        SetProgress("frames", 0);
        Task.Run(() =>
        {
            try
            {
                StopCore(reason);
            }
            catch (Exception e)
            {
                Log.Warn($"[capture] stop failed: {e}");
                _o.Status?.Invoke(Localization.Loc.Format("CaptureSaveFailed", e.Message));
            }
            finally
            {
                Volatile.Write(ref _saveStep, null);
                Volatile.Write(ref _savePercent, -1);
                Volatile.Write(ref _busy, 0);
                Idle.Set();
            }
        });
        return true;
    }

    /// <summary>End of the session: a running recording is completed (waits for it).</summary>
    public void StopAndWait(int timeoutMs = 300000)
    {
        if (_rec != null)
        {
            Stop("session end");
        }

        Idle.Wait(timeoutMs);
    }

    void StopCore(string reason)
    {
        var r = _rec!;
        r.StopNs = CaptureClock.NowNs;
        r.StopFlip = Hle.Modules.Display.FramesPresented;
        r.StopUs = GuestClock.Microseconds;
        r.StopTime = DateTime.Now;
        long cpuMs = (long)Process.GetCurrentProcess().TotalProcessorTime.TotalMilliseconds - r.ProcessCpuMs0;
        Log.Info($"[capture] stopping ({reason}) at guest frame {r.StopFlip}");
        _o.Status?.Invoke(Localization.Loc.Get("CaptureSaving"));
        // the GPU dump ends at the game's next present when one comes soon; it never waits on the guest for long (a
        // hung game presents nothing): after 2 s the worker completes the file from the commands it already received
        r.Dump?.Stop();
        Step(r, "context.txt", () => File.AppendAllText(Path.Combine(r.Dir, "context.txt"), Environment.NewLine + SafeDescribe("stop of the recording")));
        List<(string File, double StartMs)> wavs = [];
        Step(r, "audio dump", () =>
        {
            if (r.OwnsAudio)
            {
                wavs = AudioDump.Disable();
                AudioDump.VoiceDumps = false;
            }
        });
        DebugMode.CollectHleErrors = false;
        if (ReferenceEquals(CaptureHooks.Random, this))
        {
            CaptureHooks.Random = null;
        }

        Step(r, "threads_stop.txt", () => WriteText(r.Dir, "threads_stop.txt", _o.Threads));
        var closed = new ManualResetEventSlim();
        var encoderQueue = _queue;
        lock (_lock)
        {
            r.Input.Add(InputEvent.Now("mark", $"stop {reason}"));
            _rec = null;
            _queue?.Add(new Work(WorkKind.Close, default, null, 0, 0, r, closed));
            if (_preRollSeconds <= 0)
            {
                StopRunning();
            }
        }
        _o.RecordingChanged?.Invoke(false);
        var saveClock = Stopwatch.StartNew();
        // the encoder's backlog (at most EncoderQueueFrames pictures) is the first part of the progress
        int backlog = Math.Max(1, encoderQueue?.Count ?? 1);
        while (!closed.Wait(100))
        {
            SetProgress("frames", 10.0 * (1 - Math.Min(1.0, (encoderQueue?.Count ?? 0) / (double)backlog)));
            if (saveClock.ElapsedMilliseconds > 30000)
            {
                r.Notes.Add("error: the frame encoder did not finish in 30 s: frames.vsfr may be incomplete");
                break;
            }
        }
        SetProgress("gpu dump", 10);
        Step(r, "gpu dump", () =>
        {
            if (r.Dump == null || r.DumpDone.Wait(2000))
            {
                return;
            }

            if (r.Dump.StopNow(15000) is { } res)
            {
                r.DumpResult ??= res;
                r.Notes.Add("the GPU dump ended without a present (the game presented no frame in 2 s): it has the commands received until the stop");
            }
            else
            {
                r.Notes.Add("error: the GPU dump did not finish in 15 s");
            }
        });

        SetProgress("files", 15);
        string dir = r.Dir;
        List<InputEvent> input;
        lock (_lock)
        {
            input = r.Input.ToList();
        }

        Step(r, "input.tsv", () =>
        {
            using var w = new StreamWriter(Path.Combine(dir, "input.tsv"));
            w.WriteLine(InputEvent.Header);
            foreach (var e in input)
            {
                w.WriteLine(e.ToString());
            }
        });
        Step(r, "input_before.tsv", () =>
        {
            using var w = new StreamWriter(Path.Combine(dir, "input_before.tsv"));
            w.WriteLine(InputEvent.Header + "\t(the last button and touch changes before the recording started)");
            foreach (var e in r.Before)
            {
                w.WriteLine(e.ToString());
            }
        });
        Step(r, "timeline.tsv", () => WriteTimeline(r));
        Step(r, "hle_errors.txt", () => File.WriteAllLines(Path.Combine(dir, "hle_errors.txt"), DebugMode.HleSummary().DefaultIfEmpty("(no HLE error returns or unimplemented calls recorded)")));
        if (wavs.Count > 0)
        {
            Step(r, "audio/files.txt", () => File.WriteAllLines(Path.Combine(dir, "audio", "files.txt"), wavs.Select(x => FormattableString.Invariant($"{Path.GetFileName(x.File)}	{x.StartMs:F1}")).Prepend("# file	startMs (after the recording start)")));
            Step(r, "audio_mix.wav", () => CaptureAudio.Mix(wavs, Path.Combine(dir, "audio_mix.wav")));
        }
        Log.Info($"[capture] files written {dir}: {r.Frames.Count} frames ({r.FrameBytes / 1048576.0:F1} MB of pictures), {input.Count} input events" +
                 (r.DumpResult is { } d ? $", GPU dump {d.Frames} frames {d.FileBytes / 1048576.0:F1} MB" : ""));
        Log.Flush();
        Step(r, "log.txt", () => CopyLog(Path.Combine(dir, "log.txt")));
        // the analysis is optional (vitasharp-perf capture DIR writes it again): skipped when the window is closing
        double zipFrom = _o.Zip ? 60 : 99;
        var analysisClock = Stopwatch.StartNew();
        if (_hurry.IsCancellationRequested)
        {
            lock (r.Notes)
            {
                r.Notes.Add("analysis skipped: the window was closed while saving (vitasharp-perf capture <this folder> writes report.txt)");
            }
        }
        else
        {
            Step(r, "report.txt (analysis)", () =>
            {
                try
                {
                    CaptureAnalyzer.Analyze(dir, quiet: true, progress: p => SetProgress("analysis", 20 + p * (zipFrom - 20)), cancel: _hurry.Token, expectedFrames: r.Frames.Count);
                }
                catch (OperationCanceledException)
                {
                    lock (r.Notes)
                    {
                        r.Notes.Add("analysis stopped: the window was closed while saving (vitasharp-perf capture <this folder> writes report.txt)");
                    }

                    Log.Info("[capture] analysis stopped (the window is closing)");
                }
            });
        }

        double analysisS = analysisClock.Elapsed.TotalSeconds;
        // last before the zip, so it lists every step that failed
        Step(r, "session.txt", () => WriteSession(r, cpuMs));
        string? zip = null;
        if (_o.Zip)
        {
            zip = dir + ".zip";
            SetProgress("zip", zipFrom);
            try
            {
                ZipFolder(dir, zip, p => SetProgress("zip", zipFrom + p * (100 - zipFrom)));
                LastZip = zip;
            }
            catch (Exception e)
            {
                Log.Warn($"[capture] zip failed: {e.Message}");
                zip = null;
            }
        }
        Log.Info(FormattableString.Invariant($"[capture] saved {zip ?? dir} in {saveClock.Elapsed.TotalSeconds:F1} s (analysis {analysisS:F1} s{(_hurry.IsCancellationRequested ? ", hurried: the window is closing" : "")})"));
        _o.Status?.Invoke(Localization.Loc.Format("CaptureSaved", zip ?? dir));
    }

    /// <summary>One step of the save: a failure is logged and becomes an "error:" line of session.txt; the rest goes on.</summary>
    static void Step(Recording r, string what, Action a)
    {
        try
        {
            a();
        }
        catch (Exception e)
        {
            Log.Warn($"[capture] {what} failed: {e}");
            lock (r.Notes)
            {
                r.Notes.Add($"error: {what}: {e.GetType().Name}: {e.Message}");
            }
        }
    }

    void WriteTimeline(Recording r)
    {
        List<(PresentInfo Info, byte Flags, int Bytes)> frames;
        lock (r.Frames)
        {
            frames = r.Frames.ToList();
        }

        using var w = new StreamWriter(Path.Combine(r.Dir, "timeline.tsv"));
        w.WriteLine("# present\tflip\tvblank\tguestUs\tflipMs\tpresentMs\tintervalMs\tgpuMs\tdraws\tscenes\tnewShaders\tnewPipelines\thitch\thitchMs\tfb\tsize\tpreroll\tpictureBytes");
        foreach (var (p, flags, bytes) in frames)
        {
            w.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"{p.Present}\t{p.Flip}\t{p.VBlank}\t{p.GuestUs}\t{(p.FlipNs - r.StartNs) / 1e6:F3}\t{(p.PresentNs - r.StartNs) / 1e6:F3}\t{p.IntervalMs:F2}\t{p.GpuMs:F2}\t{p.Draws}\t{p.Scenes}\t{p.NewShaders}\t{p.NewPipelines}\t{(string.IsNullOrEmpty(p.Hitch) ? "-" : p.Hitch)}\t{p.HitchMs:F2}\t0x{p.Address:X8}\t{p.SrcW}x{p.SrcH}\t{((flags & FramesFile.FlagPreRoll) != 0 ? 1 : 0)}\t{bytes}"));
        }
    }

    void WriteSession(Recording r, long cpuMs)
    {
        var sb = new StringBuilder();
        double secs = (r.StopNs - r.StartNs) / 1e9;
        sb.AppendLine("VitaSharp session capture (Gravar tudo)");
        sb.AppendLine($"title-id: {_emu.TitleId}");
        sb.AppendLine($"title: {_emu.Title}");
        sb.AppendLine($"app-path: {_emu.AppPath}");
        sb.AppendLine($"platform: {_o.Platform}");
        sb.AppendLine($"options: {string.Join('\t', _o.RunOptions)}");
        sb.AppendLine(FormattableString.Invariant($"start: {r.StartTime:yyyy-MM-dd HH:mm:ss.fff}, guest frame {r.StartFlip}, guest time {r.StartUs / 1e6:F3} s"));
        sb.AppendLine(FormattableString.Invariant($"stop: {r.StopTime:yyyy-MM-dd HH:mm:ss.fff}, guest frame {r.StopFlip}, guest time {r.StopUs / 1e6:F3} s"));
        sb.AppendLine(FormattableString.Invariant($"recorded: {secs:F1} s, {r.StopFlip - r.StartFlip} guest frames ({(r.StopFlip - r.StartFlip) / Math.Max(secs, 0.001):F1} fps)"));
        sb.AppendLine($"start-ns: {r.StartNs}");
        if (r.PreRoll is { } p)
        {
            sb.AppendLine(FormattableString.Invariant($"preroll-state: state_preroll.vss guest-us {p.GuestUs} flip {p.Flip} ns {p.StartNs} ({(r.StartNs - p.StartNs) / 1e9:F1} s before the start)"));
        }
        else
        {
            sb.AppendLine(_preRollSeconds > 0 ? "preroll-state: none yet (the first one is taken 2 s after the pre-roll is armed)" : "preroll-state: none (pre-roll off)");
        }

        if (r.StartState is { } s)
        {
            sb.AppendLine(FormattableString.Invariant($"start-state: state_start.vss guest-us {s.GuestUs} flip {s.Flip} ns {s.Ns} ({r.StartStateMs:F0} ms to save)"));
        }
        else
        {
            sb.AppendLine("start-state: none");
        }

        sb.AppendLine(r.DumpResult is { } d ? $"gpu-dump: gpu{GpuDumpFormat.Extension} {d}" : "gpu-dump: none");
        int pre = r.Frames.Count(f => (f.Flags & FramesFile.FlagPreRoll) != 0);
        sb.AppendLine(FormattableString.Invariant($"frames: {r.Frames.Count} presented ({pre} from the pre-roll), {r.FrameBytes / 1048576.0:F1} MB of pictures (QOI key pictures + deltas), {r.Frames.Count(f => (f.Flags & FramesFile.FlagNoPicture) != 0)} without a picture (the encoder fell behind)"));
        sb.AppendLine($"input: {r.Input.Count} events, {r.RandomCalls} random-number results");
        lock (_stateStopMs)
        {
            if (_stateStopMs.Count > 0)
            {
                sb.AppendLine(FormattableString.Invariant($"preroll-states: {_stateStopMs.Count} taken this session, median {Median(_stateStopMs):F0} ms, max {_stateStopMs.Max():F0} ms to save each (the game stops for part of it: the 'Save state:' lines of log.txt)"));
            }
        }

        sb.AppendLine(FormattableString.Invariant($"cost: process CPU {cpuMs / 1000.0:F1} s over {secs:F1} s ({cpuMs / 10.0 / Math.Max(secs, 0.001):F0}% of one core in all, the game included)"));
        foreach (var n in r.Notes)
        {
            sb.AppendLine($"note: {n}");
        }

        if (_o.Info?.Invoke() is { Length: > 0 } info)
        {
            sb.AppendLine(info.TrimEnd());
        }

        sb.AppendLine();
        sb.AppendLine("Files:");
        sb.AppendLine("  report.txt            the analyzer's findings (black frames, flashes, repeated / out-of-order presents, stalls, audio)");
        sb.AppendLine("  frames.vsfr           every presented frame (QOI, up to 480x272) with present/flip/vblank numbers and times");
        sb.AppendLine("  timeline.tsv          one line per presented frame: timing, GPU ms, draws, stall cause");
        sb.AppendLine("  input.tsv             pad, touch, motion, random numbers, hotkeys and marks, by host time, guest time and flip");
        sb.AppendLine("  state_preroll.vss     save state at the start of the pre-roll; state_start.vss at the start of the recording");
        sb.AppendLine($"  gpu{GpuDumpFormat.Extension}              GPU dump from the start (replay-gpu)");
        sb.AppendLine("  audio/, audio_mix.wav guest audio ports and NGS voices as WAVs (files.txt: start times), and their mix");
        sb.AppendLine("  threads_*.txt         guest threads at the start and stop; hle_errors.txt; log.txt");
        sb.AppendLine("  context.txt           where the player is (screen, application in front, processes) and the audio ports with");
        sb.AppendLine("                        their owners, at the start and the stop; frames.png: a contact sheet of the recording");
        sb.AppendLine("  input_before.tsv      the last button and touch changes before the recording started");
        sb.AppendLine("Re-run (desktop): VitaSharp.Desktop rerun <this folder> [--from=preroll|start] [--window]");
        sb.AppendLine("Analyze again: vitasharp-perf capture <this folder>");
        File.WriteAllText(Path.Combine(r.Dir, "session.txt"), sb.ToString());
    }

    static double Median(List<double> v)
    {
        var s = v.OrderBy(x => x).ToList();
        return s.Count == 0 ? 0 : s[s.Count / 2];
    }

    void CopyLog(string dest)
    {
        try
        {
            if (_o.LogFile?.Invoke() is not { } src || !File.Exists(src))
            {
                File.WriteAllText(dest, "(no log file)\n");
                return;
            }
            using var input = new FileStream(src, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var output = File.Create(dest);
            input.CopyTo(output);
        }
        catch (IOException e)
        {
            Log.Warn($"[capture] log copy: {e.Message}");
        }
    }

    /// <summary>The guest threads (state, wait, recent HLE calls in debug mode) and the GPU queue, for front-ends
    /// without a richer description (Android; the desktop uses its watchdog's).</summary>
    public static string DescribeThreads(Emulator emu)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"guest frame {Hle.Modules.Display.FramesPresented}; GPU queue: submitted {emu.Gpu.Submitted}, completed {emu.Gpu.Completed}, executing {emu.Gpu.Executing?.GetType().Name ?? "nothing"}");
        try
        {
            foreach (var t in emu.Kernel.Threads.ToArray())
            {
                sb.AppendLine("thread " + t.Describe());
                foreach (var call in t.RecentHleCalls(16))
                {
                    sb.AppendLine("    hle " + call);
                }
            }
            foreach (var m in emu.Kernel.Objects.All<Kernel.Mutex>().ToArray())
            {
                if (m.Owner != null && m.Waiters > 0)
                {
                    sb.AppendLine($"mutex '{m.Name}' held by '{m.Owner.Name}' ({m.Owner.Status}) count {m.LockCount}, {m.Waiters} waiting");
                }
            }
        }
        catch (Exception e)
        {
            sb.AppendLine($"(thread list changed while reading: {e.Message})");
        }
        return sb.ToString();
    }

    void WriteText(string dir, string name, Func<string>? text)
    {
        try
        {
            File.WriteAllText(Path.Combine(dir, name), text?.Invoke() ?? DescribeThreads(_emu));
        }
        catch (Exception e)
        {
            Log.Warn($"[capture] {name}: {e.Message}");
        }
    }

    /// <summary>The folder as a zip next to it; files that are already compressed (states, dumps) are stored.</summary>
    /// <summary>The folder as a zip next to it; files that are already compressed (states, dumps, the Brotli/QOI
    /// pictures of frames.vsfr) are stored. <paramref name="progress"/> gets the fraction of bytes written (0-1).</summary>
    internal static void ZipFolder(string dir, string zip, Action<double>? progress = null)
    {
        if (File.Exists(zip))
        {
            File.Delete(zip);
        }

        var files = Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories).Select(f => new FileInfo(f)).ToList();
        double total = Math.Max(1, files.Sum(f => f.Length)), done = 0;
        using var z = ZipFile.Open(zip, ZipArchiveMode.Create);
        string baseName = Path.GetFileName(dir);
        var buf = new byte[1 << 20];
        foreach (var f in files)
        {
            string ext = f.Extension.ToLowerInvariant();
            var level = ext is ".vss" or ".vsgd" or ".vsfr" or ".png" or ".zip" ? CompressionLevel.NoCompression : CompressionLevel.Fastest;
            var entry = z.CreateEntry(Path.Combine(baseName, Path.GetRelativePath(dir, f.FullName)).Replace('\\', '/'), level);
            entry.LastWriteTime = f.LastWriteTime;
            using var input = new FileStream(f.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1 << 16);
            using var output = entry.Open();
            int n;
            while ((n = input.Read(buf, 0, buf.Length)) > 0)
            {
                output.Write(buf, 0, n);
                done += n;
                progress?.Invoke(done / total);
            }
        }
    }

    static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    static string Safe(string s) => string.Concat(s.Select(c => char.IsLetterOrDigit(c) || c is '_' or '-' ? c : '_'));

    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _preRollSeconds = 0;
            ClearRing();
            if (_rec == null)
            {
                StopRunning();
            }
        }
        _stateWake.Set();
        try
        {
            if (Directory.Exists(PreRollDir) && !Directory.EnumerateFileSystemEntries(PreRollDir).Any())
            {
                Directory.Delete(PreRollDir);
            }
        }
        catch (IOException) { }
    }
}
