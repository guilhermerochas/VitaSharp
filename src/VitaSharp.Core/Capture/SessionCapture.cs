using System.Buffers;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.IO.Compression;
using System.Text;
using VitaSharp.Core.Gpu;
using VitaSharp.Core.Host;
using VitaSharp.Core.Kernel;
using VitaSharp.Core.Memory;
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
    /// <summary>Seconds between the pre-roll's save states while they stop the game less than
    /// <see cref="PreRollStateStopBudgetMs"/> (incremental states, <see cref="IncrementalStates"/>: the pages written
    /// since the previous one); <see cref="PreRollSlowStateInterval"/> otherwise, and until three states measured it.
    /// They are kept in memory, from the newest one taken at or before the pre-roll's start on
    /// (<see cref="PreRollStateMaxBytes"/> at most), so a recording starts from a state at most one interval before its
    /// pre-roll. VITASHARP_CAPTURE_STATE_INTERVAL sets both intervals.</summary>
    public double PreRollStateInterval = 5;
    /// <summary>Seconds between the pre-roll's states that stop the game for a frame or more (a whole state: 0.1-0.35
    /// s in a game).</summary>
    public double PreRollSlowStateInterval = 10;
    /// <summary>The longest stop (median of the last three states, ms) at which the short interval is used: about
    /// one frame at 60 fps, a stop the player does not feel.</summary>
    public double PreRollStateStopBudgetMs = 16;
    /// <summary>Memory limit of the pre-roll's save states (the oldest go first; the newest is always kept).</summary>
    public long PreRollStateMaxBytes = 384L << 20;
    /// <summary>The pre-roll's states are incremental (<see cref="SnapshotPages"/>): the game stops only for the pages it
    /// wrote since the previous one, at the cost of an image of the non-zero guest memory kept between states. Off on
    /// Android (memory).</summary>
    public bool IncrementalStates = true;
    /// <summary>Every this many pre-roll states one is taken whole (the image is dropped first).</summary>
    public int FullStateEvery = 12;
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
    /// <summary>Most bytes of distinct file contents a recording keeps of what the guest read from its title's mounts
    /// (files/; <see cref="CaptureFileRecorder"/>). 0, the default: none, every read is listed with its path, offset,
    /// length and hash only (a capture is posted in public reports: it carries no file of the game); a size chosen
    /// with VITASHARP_CAPTURE_FILES_MB / --capture-files-mb=MB keeps the bytes up to it, and the report says the capture
    /// holds data of the game.</summary>
    public long MaxFileBytes;
    /// <summary>Keeps the bytes read from the title's save data too (by default only their hashes: a save is the
    /// player's); VITASHARP_CAPTURE_SAVEDATA=1 / --capture-savedata.</summary>
    public bool KeepSaveData;
    /// <summary>Also records what the microphone port captured (audio/NN_microphone_RATE.wav). Off by default: the
    /// microphone is what the player and the people near them say, and a capture goes to public reports, so only the
    /// port's format and pace go in (audio.txt); VITASHARP_CAPTURE_MIC_AUDIO=1 / --capture-mic-audio.</summary>
    public bool KeepMicrophoneAudio;
    /// <summary>Lets the location source answer while the capture is armed or recording, and keeps the --location= value
    /// in session.txt. Off by default: the position is where the player is, and a capture goes to public reports; the
    /// location service then answers "undetermined" while the capture is armed or recording, so the guest memory the
    /// capture keeps holds no new position; VITASHARP_CAPTURE_LOCATION=1 / --capture-location.</summary>
    public bool KeepLocation;
    /// <summary>Records the title's file reads (files/; a re-run's own recording does not).</summary>
    public bool Files = true;
    /// <summary>A re-run: the HLE call trace was started when its state was put back and goes on.</summary>
    public bool KeepCallTrace;
    public const double DefaultMaxSeconds = 60;
    /// <summary>What the capture's texts must not carry (paths, user, computer and account names): null = this
    /// computer's (<see cref="CapturePrivacy.ForThisComputer"/>, the data folder above <see cref="Root"/>).</summary>
    public CapturePrivacy? Privacy;
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
    private readonly Emulator _emu;
    private readonly SessionCaptureOptions _options;
    private readonly CapturePrivacy _privacy;
    private readonly object _lock = new();
    private InputRecorder? _input;
    private BlockingCollection<Work>? _queue;
    private Thread? _encoder;
    /// <summary>Tests: runs first on a new encoder thread (to hold it while the capture stops).</summary>
    internal Action? EncoderStarting { get; set; }
    /// <summary>Tests: takes the start save state in place of <see cref="SaveStates.Save"/> (the path; throws a
    /// <see cref="SnapshotException"/> when no safe point is reached).</summary>
    internal Func<string, SaveStateHeader>? SaveStartState { get; set; }
    /// <summary>Milliseconds between the tries of a start save state that found no safe point (a thread of the shell
    /// running guest code): the recording goes on and the state is taken at the first safe point.</summary>
    internal int StartStateRetryMs { get; set; } = 1000;
    private int _busy;
    private Recording? _rec;
    private bool _disposed;
    /// <summary>The title's file reads (on while armed or recording, as the tap).</summary>
    private CaptureFileRecorder? _files;
    /// <summary>The audio the game hands to its decoders (on with the file reads).</summary>
    private AudioInputRecorder? _audioInputs;

    // pre-roll
    private double _preRollSeconds;
    private Thread? _stateThread;
    private readonly AutoResetEvent _stateWake = new(false);
    private readonly SemaphoreSlim _stateGate = new(1, 1);
    private readonly LinkedList<(PresentInfo Info, byte[] Data, byte Flags)> _ringFrames = new();
    private long _ringBytes;
    private readonly LinkedList<InputEvent> _ringInput = new();
    private readonly List<PreRollState> _states = new();
    private long _stateBytes;
    /// <summary>The image of the last incremental pre-roll state (<see cref="SessionCaptureOptions.IncrementalStates"/>).</summary>
    private readonly SnapshotPages _ringPages = new();
    private int _ringStatesSinceFull;
    private bool _statesRefused;
    private long _droppedPictures;
    // timing of the pre-roll's periodic states (game stopped) for the PERF line
    private readonly List<double> _stateStopMs = new();

    /// <summary>The time to the next pre-roll state: the short interval while the last three stopped the game less
    /// than the budget, the slow one otherwise.</summary>
    internal double CurrentStateInterval => StateInterval;

    private double StateInterval
    {
        get
        {
            bool quick;
            lock (_stateStopMs)
            {
                quick = _stateStopMs.Count >= 3 && Median(_stateStopMs.GetRange(_stateStopMs.Count - 3, 3)) < _options.PreRollStateStopBudgetMs;
            }

            return Math.Max(0.01, quick ? _options.PreRollStateInterval : Math.Max(_options.PreRollStateInterval, _options.PreRollSlowStateInterval));
        }
    }

    /// <summary>A pre-roll save state, in memory (the state file's bytes).</summary>
    private sealed record PreRollState(byte[] Data, long StartNs, long GuestUs, long Flip);

    /// <summary>The pre-roll's save states taken so far this session (tests).</summary>
    internal int PreRollStatesTaken
    {
        get
        {
            lock (_stateStopMs)
            {
                return _stateStopMs.Count;
            }
        }
    }

    /// <summary>The pre-roll's save states held now (tests): how many, their bytes, the oldest's host time.</summary>
    internal (int Count, long Bytes, long OldestNs) PreRollStates
    {
        get
        {
            lock (_lock)
            {
                return (_states.Count, _stateBytes, _states.Count > 0 ? _states[0].StartNs : 0);
            }
        }
    }

    private enum WorkKind
    {
        Frame, FlushPreRoll, Close
    }

    private readonly record struct Work(WorkKind Kind, PresentInfo Info, byte[]? Pixels, int W, int H, Recording? Rec, ManualResetEventSlim? Done);

    private sealed class Recording(string dir)
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
        public CaptureFileRecorder? Files;
        public string? FilesSummary;
        public AudioInputRecorder? AudioInputs;
        public string? AudioInputsSummary;
        public GpuDumpRecorder? Dump;
        public GpuDumpResult? DumpResult;
        public readonly ManualResetEventSlim DumpDone = new(true);
        public long RandomCalls;
        public volatile bool LimitReached;
        public double StartStateMs;
        /// <summary>The tries of the start save state after the first one found no safe point, until one does or the
        /// recording stops.</summary>
        public Thread? StartStateRetry;
        public readonly ManualResetEventSlim Stopping = new(false);
        public long ProcessCpuMs0;
        /// <summary>The last pad/touch changes before the start (input_before.tsv); the last "where" line written.</summary>
        public List<InputEvent> Before = new();
        public string LastWhere = "";
        public long PresentsSeen;
        /// <summary>The player's marks of the problem (<see cref="MarkBug"/>): number, the state's guest time, host time
        /// and guest frame; the threads still saving their states.</summary>
        public readonly List<(int Number, long GuestUs, long Ns, long Flip)> Marks = new();
        public readonly List<Thread> MarkThreads = new();
        public int MarkCount;
    }

    private string SafeWhere()
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

    /// <summary>This capture turned the "waiting for input" detector (<see cref="Input.InputWaitMonitor"/>) and the detailed
    /// log (<see cref="DebugMode"/>) on.</summary>
    private bool _monitoring;

    private string SafeDescribe(string when)
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
        _options = options;
        if (Environment.GetEnvironmentVariable(EnvVars.CaptureFilesMegabytes) is { } filesMb && long.TryParse(filesMb, NumberStyles.Integer, CultureInfo.InvariantCulture, out long megabytes))
        {
            options.MaxFileBytes = Math.Max(0, megabytes) << 20;
        }

        if (Environment.GetEnvironmentVariable(EnvVars.CaptureSaveData) == "1")
        {
            options.KeepSaveData = true;
        }

        if (Environment.GetEnvironmentVariable(EnvVars.CaptureMicrophoneAudio) == "1")
        {
            options.KeepMicrophoneAudio = true;
        }

        if (Environment.GetEnvironmentVariable(EnvVars.CaptureLocation) == "1")
        {
            options.KeepLocation = true;
        }

        if (double.TryParse(Environment.GetEnvironmentVariable(EnvVars.CaptureStateInterval), NumberStyles.Float, CultureInfo.InvariantCulture, out double interval) && interval > 0)
        {
            options.PreRollStateInterval = interval;
            options.PreRollSlowStateInterval = interval;
        }
        _privacy = options.Privacy ?? CapturePrivacy.ForThisComputer(Path.GetDirectoryName(Path.GetFullPath(options.Root)), emu.Options.Psn);
        // the last button and touch changes are always kept (a compare per poll): the report says what the player did
        // just before pressing F8 even without the pre-roll
        lock (_lock)
        {
            InstallInput();
            _input!.RecordMotion = false;
        }
        EmulationPause.Changed += OnEmulationPauseChanged;
    }

    /// <summary>Pauses and resumes of the whole emulation (pause menu, save states) go into the input as marks: the
    /// analyzer then tells a long present interval spent paused from a stall of the game (a guest wait on the GPU that
    /// spans the pause is not the GPU's).</summary>
    private void OnEmulationPauseChanged(bool paused)
    {
        string data = !paused ? CaptureMarks.Resumed : EmulationPause.IsUserPaused ? CaptureMarks.Paused : CaptureMarks.PausedInternal;
        AddInput(InputEvent.Now("mark", data));
    }

    /// <summary>The last pad and touch changes while no recording runs (input_before.tsv of the next recording).</summary>
    private readonly LinkedList<InputEvent> _recent = new();
    private const int RecentInputs = 32;

    public SessionCaptureOptions Options => _options;
    public bool IsRecording => _rec != null;

    /// <summary>The location source is withheld while the capture is armed (its pre-roll keeps save states) or
    /// recording, unless <see cref="SessionCaptureOptions.KeepLocation"/> (called under the lock).</summary>
    private void UpdateLocationWithholding()
    {
        _emu.Host.LocationWithheld = !_options.KeepLocation && (_rec != null || _preRollSeconds > 0);
    }
    public bool Armed => _preRollSeconds > 0;
    public double PreRollSeconds => _preRollSeconds;
    /// <summary>The folder of the running recording, or of the last one.</summary>
    public string? Dir { get; private set; }
    /// <summary>The zip of the last completed recording.</summary>
    public string? LastZip { get; private set; }
    /// <summary>Set when the last recording was completely written (tests, scripted runs wait on it).</summary>
    public readonly ManualResetEventSlim Idle = new(true);
    /// <summary>Set once a started recording records (its HLE call trace and input run), or its start failed; reset by
    /// <see cref="Start"/>. A recording scheduled on guest frames holds the guest on it (<see cref="WaitUntilRecording"/>).</summary>
    private readonly ManualResetEventSlim _recordingBegun = new(true);
    /// <summary>Seconds since the running recording started (0 when none runs): the REC badge.</summary>
    public double RecordingSeconds => _rec is { } r ? (CaptureClock.NowNs - r.StartNs) / 1e9 : 0;
    /// <summary>Progress of the save in percent (0-100), -1 when nothing is being saved: the "saving NN%" badge.</summary>
    public int SavePercent => Volatile.Read(ref _savePercent);
    /// <summary>What the save is doing now ("frames", "analysis", "zip"...), null when nothing is being saved.</summary>
    public string? SaveStep => Volatile.Read(ref _saveStep);
    private int _savePercent = -1;
    private string? _saveStep;
    private string? _stopPending;
    private volatile bool _starting;
    private readonly CancellationTokenSource _hurry = new();

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

    private void SetProgress(string step, double pct)
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
            UpdateLocationWithholding();
            if (seconds > 0)
            {
                EnsureRunning();
                if (_stateThread == null)
                {
                    _stateThread = new Thread(PreRollStateLoop) { IsBackground = true, Name = "capture pre-roll", Priority = ThreadPriority.BelowNormal };
                    _stateThread.Start();
                }
                Log.Info(FormattableString.Invariant($"[capture] pre-roll on: frames, timing and input of the last {seconds:F0} s and a save state every {StateInterval:0.##} s (in memory) kept for Gravar tudo"));
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
    private void EnsureRunning()
    {
        if (_queue == null)
        {
            var queue = new BlockingCollection<Work>(new ConcurrentQueue<Work>());
            _queue = queue;
            // the thread gets its queue directly: a stop right after the start clears _queue before the thread runs
            _encoder = new Thread(() =>
            {
                EncoderStarting?.Invoke();
                EncoderLoop(queue);
            })
            { IsBackground = true, Name = "capture encoder" };
            _encoder.Start();
        }
        InstallInput();
        _input!.RecordMotion = true;
        _options.SetPresentTap(this);
        if (!_monitoring)
        {
            _monitoring = true;
            Input.InputWaitMonitor.CaptureStarted();
            // the detailed log is part of the capture: on while it records or keeps its pre-roll (its log.txt)
            DebugMode.CaptureStarted();
        }

        if (_options.Files && _files == null)
        {
            _files = new CaptureFileRecorder(_options.MaxFileBytes, _options.KeepSaveData);
            CaptureHooks.Files ??= _files;
            _audioInputs = new AudioInputRecorder(_options.MaxFileBytes);
            CaptureHooks.AudioInputs ??= _audioInputs;
        }
    }

    private void StopRunning()
    {
        _options.SetPresentTap(null);
        if (_monitoring)
        {
            _monitoring = false;
            Input.InputWaitMonitor.CaptureStopped();
            DebugMode.CaptureStopped();
        }

        if (_files != null && ReferenceEquals(CaptureHooks.Files, _files))
        {
            CaptureHooks.Files = null;
        }

        _files = null;
        if (_audioInputs != null && ReferenceEquals(CaptureHooks.AudioInputs, _audioInputs))
        {
            CaptureHooks.AudioInputs = null;
        }

        _audioInputs = null;
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

    /// <summary>Wraps the host's input in this capture's recorder (already done: nothing). A recorder of another
    /// capture on the same emulator (the re-run's own recording next to the window's F8 one) is replaced around the
    /// host's real input: it used to be taken as this capture's own, and left it without a recorder (a
    /// NullReferenceException in the constructor).</summary>
    private void InstallInput()
    {
        HostServices host = _emu.Host;
        if (_input != null && ReferenceEquals(host.Input, _input) && ReferenceEquals(host.Motion, _input))
        {
            return;
        }

        IInputBackend? inner = host.Input is InputRecorder r ? r.Inner : host.Input;
        IMotionSource? innerMotion = host.Motion is InputRecorder rm ? rm.InnerMotion : host.Motion;
        _input = new InputRecorder(inner, innerMotion, AddInput);
        host.Input = _input;
        host.Motion = _input;
    }

    private void UninstallInput()
    {
        HostServices host = _emu.Host;
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

    private void ClearRing()
    {
        _ringFrames.Clear();
        _ringBytes = 0;
        _ringInput.Clear();
        _states.Clear();
        _stateBytes = 0;
        _ringPages.Clear();
    }

    /// <summary>A save state every <see cref="SessionCaptureOptions.PreRollStateInterval"/> while armed and not
    /// recording, into memory (<see cref="TrimStates"/>: from the newest one at or before the pre-roll's start on), so
    /// the ring always starts at a state at most one interval before the pre-roll.</summary>
    private void PreRollStateLoop()
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
            _stateWake.WaitOne(TimeSpan.FromSeconds(_states.Count == 0 ? Math.Min(2, StateInterval) : StateInterval));
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
                long ns = CaptureClock.NowNs, flip = Hle.Modules.Display.FramesPresented;
                AddInput(InputEvent.Now("mark", "preroll_state begin"));
                using var stream = new MemoryStream();
                SnapshotPages? image = null;
                if (_options.IncrementalStates)
                {
                    if (++_ringStatesSinceFull >= Math.Max(1, _options.FullStateEvery))
                    {
                        _ringStatesSinceFull = 0;
                        _ringPages.Clear();
                    }

                    image = _ringPages;
                }

                SaveStateHeader header = SaveStates.Save(_emu, stream, image, null, 2000);
                lock (_stateStopMs)
                {
                    _stateStopMs.Add(SaveStates.LastStop.HeldMs);
                }

                AddInput(InputEvent.Now("mark", "preroll_state"));
                byte[] data = stream.ToArray();
                lock (_lock)
                {
                    if (_rec == null && _preRollSeconds > 0)
                    {
                        _states.Add(new PreRollState(data, ns, (long)(header.GuestSeconds * 1e6), flip));
                        _stateBytes += data.Length;
                        TrimStates();
                        TrimRing();
                    }
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
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or OutOfMemoryException)
            {
                Log.Warn($"[capture] pre-roll save state: {e.Message}");
            }
            finally
            {
                _stateGate.Release();
            }
        }
    }

    /// <summary>Keeps the pre-roll's states from the newest one taken at or before the pre-roll's start on (it covers
    /// the whole pre-roll; the older ones are not needed), and within <see cref="SessionCaptureOptions.PreRollStateMaxBytes"/>
    /// (the oldest go first, the newest always stays). Under <see cref="_lock"/>.</summary>
    private void TrimStates()
    {
        long cover = CaptureClock.NowNs - (long)(1e9 * _preRollSeconds);
        int covering = -1;
        for (int i = 0; i < _states.Count; i++)
        {
            if (_states[i].StartNs <= cover)
            {
                covering = i;
            }
        }

        while (covering > 0 || (_stateBytes > _options.PreRollStateMaxBytes && _states.Count > 1))
        {
            _stateBytes -= _states[0].Data.Length;
            _states.RemoveAt(0);
            covering--;
        }
    }

    /// <summary>Drops pre-roll items older than the oldest kept state (or than twice the period without one), and the
    /// oldest frames over the memory limit. Under <see cref="_lock"/>.</summary>
    private void TrimRing()
    {
        long now = CaptureClock.NowNs;
        long keepFrom = Math.Min(_states.Count > 0 ? _states[0].StartNs : long.MaxValue, now - (long)(1e9 * Math.Max(_preRollSeconds, _states.Count > 0 ? 0 : 2 * StateInterval)));
        while (_ringFrames.First is { } f && (f.Value.Info.PresentNs < keepFrom || _ringBytes > _options.PreRollMaxBytes))
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

        if (_states.Count > 0)
        {
            _files?.Trim(_states[0].StartNs);
            _audioInputs?.Trim(_states[0].StartNs);
        }
    }

    // ------------------------------------------------------------------ data in

    private void AddInput(InputEvent inputEvent)
    {
        lock (_lock)
        {
            if (_rec is { } r)
            {
                r.Input.Add(inputEvent);
                return;
            }
            if (inputEvent.Kind is "pad" or "touch")
            {
                _recent.AddLast(inputEvent);
                if (_recent.Count > RecentInputs)
                {
                    _recent.RemoveFirst();
                }
            }
            if (_preRollSeconds > 0)
            {
                _ringInput.AddLast(inputEvent);
                if (_ringInput.Count % 256 == 0)
                {
                    TrimRing();
                }
            }
        }
    }

    /// <summary>
    /// The player marks the problem while recording (Shift+F8 on the desktop, the pause menu on Android): a mark in
    /// input.tsv at once, and a save state of that moment, <c>state_mark_N.vss</c>, taken on a thread of its own (the
    /// game stops 0.1-0.35 s, as for any state) and listed in session.txt (<c>mark-state-N</c>), so a re-run can start
    /// right there (<c>rerun --from=markN</c>). False when no recording runs.
    /// </summary>
    public bool MarkBug(string reason)
    {
        Recording? r = _rec;
        if (r == null || r.Stopping.IsSet)
        {
            _options.Status?.Invoke(Localization.Loc.Get("CaptureMarkNoRecording"));
            return false;
        }

        int number = Interlocked.Increment(ref r.MarkCount);
        AddInput(InputEvent.Now("mark", $"bug {number} {reason}"));
        var thread = new Thread(() => TakeMarkState(r, number)) { IsBackground = true, Name = "capture mark" };
        lock (_lock)
        {
            r.MarkThreads.Add(thread);
        }

        thread.Start();
        Log.Info($"[capture] the player marked the problem ({reason}): mark {number}");
        return true;
    }

    private void TakeMarkState(Recording r, int number)
    {
        if (!_stateGate.Wait(5000))
        {
            lock (_lock)
            {
                r.Notes.Add($"mark {number}: no save state (another state was being saved)");
            }

            return;
        }

        try
        {
            long ns = CaptureClock.NowNs, flip = Hle.Modules.Display.FramesPresented;
            string path = Path.Combine(r.Dir, CaptureStates.FileName(CaptureStates.Mark(number)));
            SaveStateHeader header = SaveStates.Save(_emu, path, null, 2000);
            lock (_lock)
            {
                r.Marks.Add((number, (long)(header.GuestSeconds * 1e6), ns, flip));
                r.Input.Add(InputEvent.Now("mark", $"state_mark_{number}"));
            }

            _options.Status?.Invoke(Localization.Loc.Format("CaptureMarked", number));
        }
        catch (Exception e) when (e is SnapshotException or IOException or UnauthorizedAccessException)
        {
            lock (_lock)
            {
                r.Notes.Add($"mark {number}: no save state ({e.Message})");
            }
        }
        finally
        {
            _stateGate.Release();
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
        BlockingCollection<Work>? q = _queue;
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
        if (q.Count < _options.EncoderQueueFrames && rgba.Length >= w * h * 4)
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
    private void CheckLimit(Recording r)
    {
        double secs = (CaptureClock.NowNs - r.StartNs) / 1e9;
        bool time = _options.MaxSeconds > 0 && secs >= _options.MaxSeconds;
        bool size = _options.MaxBytes > 0 && Interlocked.Read(ref r.FrameBytes) >= _options.MaxBytes;
        if (!time && !size)
        {
            return;
        }

        r.LimitReached = true;
        string why = time ? FormattableString.Invariant($"{_options.MaxSeconds:F0} s") : $"{_options.MaxBytes >> 20} MB";
        Log.Info($"[capture] limit reached ({why}): the recording stops and saves by itself");
        lock (r.Notes)
        {
            r.Notes.Add($"stopped by the limit ({why}; VITASHARP_CAPTURE_MAX_SECONDS / --capture-max=SECONDS change it, 0 = none)");
        }

        _options.Status?.Invoke(Localization.Loc.Format("CaptureLimit", why));
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

    private void EncoderLoop(BlockingCollection<Work> q)
    {
        var enc = new FramesFile.Encoder();
        foreach (Work work in q.GetConsumingEnumerable())
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

                        foreach ((PresentInfo info, byte[]? bytes, byte fl) in ring)
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

    private static void WriteFrame(Recording r, in PresentInfo info, byte flags, byte[] data)
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
            _options.Status?.Invoke(Localization.Loc.Get("CaptureWait"));
            return false;
        }
        if (_rec != null)
        {
            Volatile.Write(ref _busy, 0);
            return false;
        }
        Idle.Reset();
        _recordingBegun.Reset();
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
                _options.Status?.Invoke(Localization.Loc.Format("CaptureStartFailed", e.Message));
                Idle.Set();
            }
            finally
            {
                _recordingBegun.Set();
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

    private void StartCore(string reason, bool limit)
    {
        string title = Safe(string.IsNullOrEmpty(_emu.TitleId) ? "NOTITLE" : _emu.TitleId);
        string baseName = $"{title}_{DateTime.Now:yyyyMMdd_HHmmss}_tudo";
        string dir = Path.Combine(_options.Root, baseName);
        for (int i = 2; Directory.Exists(dir) || File.Exists(dir + ".zip"); i++)
        {
            dir = Path.Combine(_options.Root, $"{baseName}_{i}");
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
            foreach (InputEvent inputEvent in _ringInput)
            {
                if (inputEvent.Ns >= from)
                {
                    r.Input.Add(inputEvent);
                }
            }

            _ringInput.Clear();
            r.Before = _recent.ToList();
            r.Files = _files;
            r.AudioInputs = _audioInputs;
            _recent.Clear();
            r.Input.Add(InputEvent.Now("mark", $"start {reason}"));
            r.Input.Add(InputEvent.Now("ctx", where));
            r.LastWhere = where;
            _rec = r;
            UpdateLocationWithholding();
            _queue!.Add(new Work(WorkKind.FlushPreRoll, default, null, 0, 0, r, null));
        }
        Log.Info($"[capture] recording ({reason}) at guest frame {r.StartFlip}: {dir}" + (pre != null ? $"; pre-roll from guest frame {pre.Flip} ({(r.StartNs - pre.StartNs) / 1e9:F1} s before)" : _preRollSeconds > 0 ? "; pre-roll without a save state yet" : ""));
        _options.RecordingChanged?.Invoke(true);
        _options.Status?.Invoke(Localization.Loc.Format("CaptureRecording", _options.StopHint));
        if (pre != null)
        {
            try
            {
                File.WriteAllBytes(Path.Combine(dir, "state_preroll.vss"), pre.Data);
            }
            catch (IOException e)
            {
                r.Notes.Add($"pre-roll state not written: {e.Message}");
                r.PreRoll = null;
            }
        }
        if (!AudioDump.Enabled)
        {
            AudioDump.Enable(Path.Combine(dir, "audio"));
            AudioDump.VoiceDumps = true;
            AudioDump.MicrophoneDumps = _options.KeepMicrophoneAudio;
            r.OwnsAudio = true;
        }
        else
        {
            r.Notes.Add("audio: --audio-dump / VITASHARP_AUDIO_DUMP was already recording the ports elsewhere; no audio/ in this capture");
        }

        DebugMode.CollectHleErrors = true;
        // a re-run's trace began when its state was put back (SaveStates.Restored), before the game ran again
        if (!_options.KeepCallTrace || !HleCallTrace.Active)
        {
            HleCallTrace.Begin();
        }
        CaptureHooks.Random ??= this;
        // what follows (the start save state, the GPU dump) waits for the guest: a guest held at the frame the
        // recording was scheduled for goes on from here
        _recordingBegun.Set();
        WriteText(dir, "threads_start.txt", _options.Threads);
        WriteText(dir, "context.txt", () => SafeDescribe("start of the recording"));

        if (_options.StartState && !TryStartState(r, 3000, out string? notSafe))
        {
            // a thread running guest code (the shell with its apps) can keep the first tries from a safe point: the
            // recording goes on and the state is taken at the first one (a re-run starts there)
            Log.Info($"[capture] start save state: {notSafe}; trying again every {StartStateRetryMs} ms");
            r.StartStateRetry = new Thread(() => RetryStartState(r, notSafe)) { IsBackground = true, Name = "capture-start-state" };
            r.StartStateRetry.Start();
        }

        if (_options.GpuDump && _rec == r)
        {
            r.DumpDone.Reset();
            r.Dump = GpuDumpRecorder.Start(new GpuDumpOptions
            {
                Queue = _emu.Gpu,
                Path = Path.Combine(dir, "gpu" + GpuDumpFormat.Extension),
                Header = RedactHeader(_options.DumpHeader?.Invoke() ?? new GpuDumpHeader()),
                MaxFrames = _options.DumpMaxFrames,
                Finished = res =>
                {
                    r.DumpResult = res;
                    r.DumpDone.Set();
                },
            });
            if (r.Dump == null)
            {
                r.DumpDone.Set();
                r.Notes.Add("no GPU dump: another GPU dump (Ctrl+F8) was recording");
            }
        }
    }

    /// <summary>Waits until the recording <see cref="Start"/> began records (or failed to start), at most
    /// <paramref name="timeoutMs"/>: the start runs in the background, and a guest that goes on meanwhile would run
    /// frames the recording misses. False when it did not begin in time.</summary>
    public bool WaitUntilRecording(int timeoutMs) => _recordingBegun.Wait(timeoutMs) && _rec != null;

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
                _options.Status?.Invoke(Localization.Loc.Get("CaptureStarting"));
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
                _options.Status?.Invoke(Localization.Loc.Format("CaptureSaveFailed", e.Message));
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

    /// <summary>Takes state_start.vss and marks it in the input log; false (with the reason) when no safe point was
    /// reached in <paramref name="timeoutMs"/>.</summary>
    private bool TryStartState(Recording r, int timeoutMs, out string? why)
    {
        why = null;
        if (!_stateGate.Wait(5000))
        {
            why = "the pre-roll was saving a state";
            return false;
        }

        try
        {
            var sw = Stopwatch.StartNew();
            long ns = CaptureClock.NowNs, flip = Hle.Modules.Display.FramesPresented;
            lock (_lock)
            {
                r.Input.Add(InputEvent.Now("mark", "state_start begin"));
            }

            string path = Path.Combine(r.Dir, "state_start.vss");
            SaveStateHeader saveStateHeader = SaveStartState != null ? SaveStartState(path) : SaveStates.Save(_emu, path, null, timeoutMs);
            r.StartStateMs = sw.Elapsed.TotalMilliseconds;
            r.StartState = ((long)(saveStateHeader.GuestSeconds * 1e6), ns, flip);
            lock (_lock)
            {
                r.Input.Add(InputEvent.Now("mark", "state_start"));
            }

            return true;
        }
        catch (SnapshotException e)
        {
            why = e.Message;
            return false;
        }
        finally
        {
            _stateGate.Release();
        }
    }

    /// <summary>Tries the start save state again until a safe point or the stop; the notes say when it was taken.</summary>
    private void RetryStartState(Recording r, string? firstWhy)
    {
        int tries = 1;
        string? why = firstWhy;
        while (!r.Stopping.Wait(StartStateRetryMs))
        {
            tries++;
            if (TryStartState(r, 1000, out why))
            {
                double late = (CaptureClock.NowNs - r.StartNs) / 1e9;
                lock (r.Notes)
                {
                    r.Notes.Add(FormattableString.Invariant(
                        $"start save state taken {late:F1} s after the start, at try {tries} (before: {firstWhy}); a re-run starts there"));
                }

                Log.Info(FormattableString.Invariant($"[capture] start save state taken {late:F1} s after the start (try {tries})"));
                return;
            }
        }

        lock (r.Notes)
        {
            r.Notes.Add($"no start save state: {why} ({tries} tries until the stop)");
        }

        Log.Info($"[capture] no start save state: {why} ({tries} tries)");
    }

    private void StopCore(string reason)
    {
        Recording r = _rec!;
        // a start save state still being retried stops trying (one being written completes first)
        r.Stopping.Set();
        r.StartStateRetry?.Join(10_000);
        // the marks' states being saved are in the capture (session.txt lists them)
        Thread[] marking;
        lock (_lock)
        {
            marking = r.MarkThreads.ToArray();
        }

        foreach (Thread t in marking)
        {
            t.Join(10_000);
        }
        r.StopNs = CaptureClock.NowNs;
        r.StopFlip = Hle.Modules.Display.FramesPresented;
        r.StopUs = GuestClock.Microseconds;
        r.StopTime = DateTime.Now;
        long cpuMs = (long)Process.GetCurrentProcess().TotalProcessorTime.TotalMilliseconds - r.ProcessCpuMs0;
        Log.Info($"[capture] stopping ({reason}) at guest frame {r.StopFlip}");
        _options.Status?.Invoke(Localization.Loc.Get("CaptureSaving"));
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
                AudioDump.MicrophoneDumps = false;
            }
        });
        DebugMode.CollectHleErrors = false;
        HleCallTrace.End();
        if (ReferenceEquals(CaptureHooks.Random, this))
        {
            CaptureHooks.Random = null;
        }

        Step(r, "threads_stop.txt", () => WriteText(r.Dir, "threads_stop.txt", _options.Threads));
        var closed = new ManualResetEventSlim();
        BlockingCollection<Work>? encoderQueue = _queue;
        lock (_lock)
        {
            r.Input.Add(InputEvent.Now("mark", $"stop {reason}"));
            _rec = null;
            UpdateLocationWithholding();
            _queue?.Add(new Work(WorkKind.Close, default, null, 0, 0, r, closed));
            if (_preRollSeconds <= 0)
            {
                StopRunning();
            }
        }
        _options.RecordingChanged?.Invoke(false);
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
            foreach (InputEvent e in input)
            {
                w.WriteLine(e.ToString());
            }
        });
        Step(r, "input_before.tsv", () =>
        {
            using var w = new StreamWriter(Path.Combine(dir, "input_before.tsv"));
            w.WriteLine(InputEvent.Header + "\t(the last button and touch changes before the recording started)");
            foreach (InputEvent e in r.Before)
            {
                w.WriteLine(e.ToString());
            }
        });
        Step(r, "timeline.tsv", () => WriteTimeline(r));
        List<string> callSummary = [];
        Step(r, HleCallTrace.FileName, () => callSummary = HleCallTrace.Write(Path.Combine(dir, HleCallTrace.FileName)));
        Step(r, CaptureFileRecorder.Folder + "/", () =>
        {
            if (r.Files != null)
            {
                r.FilesSummary = r.Files.Write(dir, r.PreRoll?.StartNs ?? r.StartNs).ToString();
            }
        });
        Step(r, AudioInputRecorder.Folder + "/", () => r.AudioInputsSummary = r.AudioInputs?.Write(dir, r.PreRoll?.StartNs ?? r.StartNs));
        Step(r, "io_failures.txt", () => File.WriteAllLines(Path.Combine(dir, "io_failures.txt"), _emu.Hle.Modules.OfType<Hle.Modules.IoFileMgr>().FirstOrDefault()?.Failures.Lines() ?? ["(no IoFileMgr)"]));
        Step(r, "hle_errors.txt", () => File.WriteAllLines(Path.Combine(dir, "hle_errors.txt"), DebugMode.HleSummary().DefaultIfEmpty("(no HLE error returns or unimplemented calls recorded)").Concat(callSummary)));
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
        double zipFrom = _options.Zip ? 60 : 99;
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
        // nothing of the person who recorded it leaves with the capture (paths, user, computer and account names)
        Step(r, "privacy", () => _privacy.RedactFolder(dir));
        string? zip = null;
        if (_options.Zip)
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
        _options.Status?.Invoke(Localization.Loc.Format("CaptureSaved", zip ?? dir));
    }

    /// <summary>One step of the save: a failure is logged and becomes an "error:" line of session.txt; the rest goes on.</summary>
    private static void Step(Recording r, string what, Action action)
    {
        try
        {
            action();
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

    private void WriteTimeline(Recording r)
    {
        List<(PresentInfo Info, byte Flags, int Bytes)> frames;
        lock (r.Frames)
        {
            frames = r.Frames.ToList();
        }

        using var w = new StreamWriter(Path.Combine(r.Dir, "timeline.tsv"));
        w.WriteLine("# present\tflip\tvblank\tguestUs\tflipMs\tpresentMs\tintervalMs\tgpuMs\tdraws\tscenes\tnewShaders\tnewPipelines\thitch\thitchMs\tfb\tsize\tpreroll\tpictureBytes");
        foreach ((PresentInfo presentInfo, byte flags, int bytes) in frames)
        {
            w.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"{presentInfo.Present}\t{presentInfo.Flip}\t{presentInfo.VBlank}\t{presentInfo.GuestUs}\t{(presentInfo.FlipNs - r.StartNs) / 1e6:F3}\t{(presentInfo.PresentNs - r.StartNs) / 1e6:F3}\t{presentInfo.IntervalMs:F2}\t{presentInfo.GpuMs:F2}\t{presentInfo.Draws}\t{presentInfo.Scenes}\t{presentInfo.NewShaders}\t{presentInfo.NewPipelines}\t{(string.IsNullOrEmpty(presentInfo.Hitch) ? "-" : presentInfo.Hitch)}\t{presentInfo.HitchMs:F2}\t0x{presentInfo.Address:X8}\t{presentInfo.SrcW}x{presentInfo.SrcH}\t{((flags & FramesFile.FlagPreRoll) != 0 ? 1 : 0)}\t{bytes}"));
        }
    }

    private void WriteSession(Recording r, long cpuMs)
    {
        var sb = new StringBuilder();
        double secs = (r.StopNs - r.StartNs) / 1e9;
        sb.AppendLine("VitaSharp session capture (Gravar tudo)");
        sb.AppendLine($"title-id: {_emu.TitleId}");
        sb.AppendLine($"title: {_emu.Title}");
        sb.AppendLine($"app-path: {_privacy.Redact(_emu.AppPath)}");
        sb.AppendLine($"platform: {_options.Platform}");
        sb.AppendLine($"options: {string.Join('\t', _privacy.RedactOptions(_options.RunOptions, _options.KeepLocation))}");
        foreach (string line in CaptureIdentity.Describe(_emu))
        {
            sb.AppendLine(_privacy.Redact(line));
        }

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

        lock (_lock)
        {
            foreach ((int number, long guestUs, long ns, long flip) in r.Marks)
            {
                sb.AppendLine(FormattableString.Invariant($"{CaptureStates.SessionKey(CaptureStates.Mark(number))}: {CaptureStates.FileName(CaptureStates.Mark(number))} guest-us {guestUs} flip {flip} ns {ns} ({(ns - r.StartNs) / 1e9:F1} s after the start; marked by the player)"));
            }
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
        // the touch path since the emulator started: raw host events and what the touch driver handed the guest
        sb.AppendLine(Input.TouchDiagnostics.Describe());
        sb.AppendLine($"files: {r.FilesSummary ?? "none recorded"}");
        sb.AppendLine($"audio-inputs: {r.AudioInputsSummary ?? "none recorded"}");
        lock (_stateStopMs)
        {
            if (_stateStopMs.Count > 0)
            {
                sb.AppendLine(FormattableString.Invariant($"preroll-states: {_stateStopMs.Count} taken this session, median {Median(_stateStopMs):F1} ms, max {_stateStopMs.Max():F1} ms with the game held for each ({(_options.IncrementalStates ? "incremental: only the pages written since the previous one are read then" : "whole")}; the 'Save state:' lines of log.txt)"));
            }
        }

        sb.AppendLine(FormattableString.Invariant($"cost: process CPU {cpuMs / 1000.0:F1} s over {secs:F1} s ({cpuMs / 10.0 / Math.Max(secs, 0.001):F0}% of one core in all, the game included)"));
        foreach (string n in r.Notes)
        {
            sb.AppendLine($"note: {n}");
        }

        if (_options.Info?.Invoke() is { Length: > 0 } info)
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
        sb.AppendLine("  io_failures.txt       every failed open/dopen/getstat/chstat/remove/mkdir/rmdir/rename since the boot (guest path, error, count)");
        sb.AppendLine("  files/                what the game read from its own mounts (app0, savedata0, addcont0...): files.tsv, blobs.bin");
        sb.AppendLine($"  {HleCallTrace.FileName}         every HLE call of the recording: thread, function, r0-r3, result, return address");
        sb.AppendLine("  context.txt           where the player is (screen, application in front, processes) and the audio ports with");
        sb.AppendLine("                        their owners, at the start and the stop; frames.png: a contact sheet of the recording");
        sb.AppendLine("  input_before.tsv      the last button and touch changes before the recording started");
        sb.AppendLine("Re-run (desktop): VitaSharp.Desktop rerun <this folder> [--from=preroll|start|markN] [--window]");
        sb.AppendLine("Analyze again: vitasharp-perf capture <this folder>");
        File.WriteAllText(Path.Combine(r.Dir, "session.txt"), sb.ToString());
    }

    /// <summary>The GPU dump's header without the personal items (its app path, the command line).</summary>
    private GpuDumpHeader RedactHeader(GpuDumpHeader header)
    {
        for (int i = 0; i < header.Entries.Count; i++)
        {
            KeyValuePair<string, string> entry = header.Entries[i];
            header.Entries[i] = new(entry.Key, _privacy.Redact(entry.Value));
        }

        return header;
    }

    private static double Median(List<double> v)
    {
        var s = v.OrderBy(x => x).ToList();
        return s.Count == 0 ? 0 : s[s.Count / 2];
    }

    private void CopyLog(string dest)
    {
        try
        {
            if (_options.LogFile?.Invoke() is not { } src || !File.Exists(src))
            {
                File.WriteAllText(dest, "(no log file)\n");
                return;
            }
            using var input = new FileStream(src, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using FileStream output = File.Create(dest);
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
            foreach (GuestThread? t in emu.Kernel.Threads.ToArray())
            {
                sb.AppendLine("thread " + t.Describe());
                foreach (string call in t.RecentHleCalls(16))
                {
                    sb.AppendLine("    hle " + call);
                }
            }
            foreach (Kernel.Mutex? m in emu.Kernel.Objects.All<Kernel.Mutex>().ToArray())
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

    private void WriteText(string dir, string name, Func<string>? text)
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
        using ZipArchive z = ZipFile.Open(zip, ZipArchiveMode.Create);
        string baseName = Path.GetFileName(dir);
        byte[] buf = new byte[1 << 20];
        foreach (FileInfo? f in files)
        {
            string ext = f.Extension.ToLowerInvariant();
            CompressionLevel level = ext is ".vss" or ".vsgd" or ".vsfr" or ".png" or ".zip" ? CompressionLevel.NoCompression : CompressionLevel.Fastest;
            ZipArchiveEntry entry = z.CreateEntry(Path.Combine(baseName, Path.GetRelativePath(dir, f.FullName)).Replace('\\', '/'), level);
            entry.LastWriteTime = f.LastWriteTime;
            using var input = new FileStream(f.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1 << 16);
            using Stream output = entry.Open();
            int n;
            while ((n = input.Read(buf, 0, buf.Length)) > 0)
            {
                output.Write(buf, 0, n);
                done += n;
                progress?.Invoke(done / total);
            }
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static string Safe(string s) => string.Concat(s.Select(c => char.IsLetterOrDigit(c) || c is '_' or '-' ? c : '_'));

    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            EmulationPause.Changed -= OnEmulationPauseChanged;
            _preRollSeconds = 0;
            _emu.Host.LocationWithheld = false;
            ClearRing();
            if (_rec == null)
            {
                StopRunning();
            }
        }
        _stateWake.Set();
    }
}

/// <summary>The save states of a capture by name: <c>preroll</c>, <c>start</c> and <c>markN</c> (the player's N-th
/// mark), their files and their session.txt keys (rerun --from=, --capture-playback=DIR@).</summary>
public static class CaptureStates
{
    public static string Mark(int number) => FormattableString.Invariant($"mark{number}");

    /// <summary>preroll, start or markN (N >= 1).</summary>
    public static bool IsValid(string from) =>
        from is "preroll" or "start" || (from.StartsWith("mark", StringComparison.Ordinal) && int.TryParse(from.AsSpan(4), NumberStyles.None, CultureInfo.InvariantCulture, out int n) && n >= 1);

    public static string FileName(string from) => from switch
    {
        "preroll" => "state_preroll.vss",
        "start" => "state_start.vss",
        _ => $"state_mark_{from[4..]}.vss",
    };

    public static string SessionKey(string from) => from switch
    {
        "preroll" => "preroll-state",
        "start" => "start-state",
        _ => $"mark-state-{from[4..]}",
    };
}
