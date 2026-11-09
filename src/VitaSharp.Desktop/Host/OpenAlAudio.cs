using Silk.NET.OpenAL;
using VitaSharp.Core.Host;
using VitaSharp.Core.Util;

namespace VitaSharp.Desktop.Host;

/// <summary>
/// OpenAL Soft output: one streaming source per guest port. OpenAL Soft mixes in fixed updates (ALC_FREQUENCY /
/// ALC_REFRESH samples, typically 10-21 ms) and a source only gives back fully played buffers when an update runs,
/// so each stream keeps at least two updates plus two grains queued and (re)starts playing only once one update plus
/// a grain is buffered. A fixed depth of 4 grains (1024 samples for a 256-sample grain, about one update) let every
/// update drain the whole queue: the source stopped and restarted ~25 times a second (crackling, stutter).
/// </summary>
public sealed unsafe class OpenAlAudio : IAudioBackend
{
    const int ALC_FREQUENCY = 0x1007, ALC_REFRESH = 0x1008;
    readonly AL _al;
    readonly ALContext _alc;
    readonly Device* _device;
    readonly Context* _context;
    readonly object _lock = new();
    string _deviceName;
    /// <summary>Mixer rate and samples per mix update of the device.</summary>
    readonly int _mixRate, _updateSamples;
    readonly List<Stream> _streams = new();

    OpenAlAudio(AL al, ALContext alc, Device* device, Context* context, AudioOutputFollower follower)
    {
        _al = al; _alc = alc; _device = device; _context = context; _follower = follower;
        _deviceName = OpenedName(alc, device) ?? "?";
        int freq = 0, refresh = 0;
        try
        {
            alc.GetContextProperty(device, (GetContextInteger)ALC_FREQUENCY, 1, &freq);
            alc.GetContextProperty(device, (GetContextInteger)ALC_REFRESH, 1, &refresh);
        }
        catch (Exception) { }
        _mixRate = freq > 0 ? freq : 48000;
        // unknown refresh: assume OpenAL Soft's default period of 1024 frames
        _updateSamples = refresh > 0 ? Math.Max(1, _mixRate / refresh) : 1024;
    }

    const int ALC_DEFAULT_ALL_DEVICES_SPECIFIER = 0x1012, ALC_ALL_DEVICES_SPECIFIER = 0x1013;

    /// <summary>
    /// Output device names (ALC_ENUMERATE_ALL_EXT) and the system default. Game controllers with a headset jack or a
    /// speaker (DualShock 4, DualSense, Xbox) show up as audio outputs and Windows can make them the default.
    /// </summary>
    static List<string> ListDevices(ALContext alc, out string? defaultName)
    {
        var names = new List<string>();
        defaultName = null;
        var getString = (delegate* unmanaged[Cdecl]<Device*, int, byte*>)alc.Context.GetProcAddress("alcGetString");
        if (getString == null) return names;
        byte* p = getString(null, ALC_ALL_DEVICES_SPECIFIER);
        while (p != null && *p != 0)
        {
            var s = System.Runtime.InteropServices.Marshal.PtrToStringUTF8((nint)p)!;
            names.Add(s);
            p += System.Text.Encoding.UTF8.GetByteCount(s) + 1;
        }
        byte* d = getString(null, ALC_DEFAULT_ALL_DEVICES_SPECIFIER);
        if (d != null) defaultName = System.Runtime.InteropServices.Marshal.PtrToStringUTF8((nint)d);
        return names;
    }

    /// <summary>The full name of an open output: ALC_ALL_DEVICES_SPECIFIER on the device (ALC_DEVICE_SPECIFIER gives
    /// only "OpenAL Soft" on Windows).</summary>
    static string? OpenedName(ALContext alc, Device* device)
    {
        try
        {
            var getString = (delegate* unmanaged[Cdecl]<Device*, int, byte*>)alc.Context.GetProcAddress("alcGetString");
            byte* p = getString == null ? null : getString(device, ALC_ALL_DEVICES_SPECIFIER);
            return p == null ? null : System.Runtime.InteropServices.Marshal.PtrToStringUTF8((nint)p);
        }
        catch (Exception) { return null; }
    }

    /// <summary>The output names and the system default, for the launcher's "Saida de audio" list (empty when OpenAL
    /// cannot be loaded).</summary>
    public static (List<string> Outputs, string? Default) ListOutputs()
    {
        try
        {
            var alc = ALContext.GetApi(true);
            var names = ListDevices(alc, out var def);
            return (names, def);
        }
        catch (Exception e)
        {
            Log.Warn($"OpenAL unavailable: {e.Message}");
            return ([], null);
        }
    }

    public static OpenAlAudio? TryCreate(string? requestedDevice = null)
    {
        try
        {
            var alc = ALContext.GetApi(true);
            var al = AL.GetApi(true);
            var follower = new AudioOutputFollower(requestedDevice ?? Environment.GetEnvironmentVariable(EnvVars.AudioDevice), GameControllerAudio.Predicate());
            var names = ListDevices(alc, out var def);
            string name = follower.Initial(names, def, out var why);
            Log.Info(why);
            var dev = alc.OpenDevice(name);
            if (dev == null && name.Length > 0) dev = alc.OpenDevice("");
            if (dev == null) { Log.Warn("OpenAL: no output device"); return null; }
            var ctx = alc.CreateContext(dev, null);
            alc.MakeContextCurrent(ctx);
            var a = new OpenAlAudio(al, alc, dev, ctx, follower);
            Log.Info($"Audio: OpenAL Soft, {a.Describe()}");
            AudioDiagnostics.DeviceInfo = a.Describe;
            a.StartWatching();
            return a;
        }
        catch (Exception e)
        {
            Log.Warn($"OpenAL unavailable: {e.Message}");
            return null;
        }
    }

    /// <summary>Device, mixer settings and per-stream buffering (debug lines, F10 audio.txt, session summary).</summary>
    public string Describe()
    {
        var sb = new System.Text.StringBuilder($"OpenAL '{_deviceName}' mixer {_mixRate} Hz, update {_updateSamples} samples ({_updateSamples * 1000.0 / _mixRate:F1} ms)");
        lock (_lock)
            foreach (var s in _streams)
                sb.Append($"; stream {s.Rate} Hz grain {s.Grain}: up to {s.Capacity} grains queued ({s.Capacity * s.Grain * 1000.0 / s.Rate:F1} ms), " +
                          $"starts at {s.StartGrains}, latency ~{s.StartGrains * s.Grain * 1000.0 / s.Rate + _updateSamples * 1000.0 / _mixRate:F0} ms, underruns {s.Underruns}");
        return sb.ToString();
    }

    public IAudioStream OpenStream(int sampleRate, int channels, int grainSamples)
    {
        lock (_lock)
        {
            _alc.MakeContextCurrent(_context);
            var s = new Stream(this, sampleRate, grainSamples);
            _streams.Add(s);
            return s;
        }
    }

    /// <summary>Times any output stream ran dry and was restarted (diagnostics for audio/video pacing).</summary>
    public static int TotalUnderruns;

    /// <summary>The emulation is paused: the guest's sources are paused (AL_PAUSED keeps the queued grains and is not a
    /// stop, so no underrun is counted) and none starts playing until the resume.</summary>
    volatile bool _paused;

    public void SetPaused(bool paused)
    {
        lock (_lock)
        {
            if (_paused == paused) return;
            _paused = paused;
            _alc.MakeContextCurrent(_context);
            foreach (var s in _streams) s.SetPaused(paused);
        }
    }

    // one-shot host sound effects (the trophy chime): a static source of their own, never one of the guest's ports
    uint _fxSource, _fxBuffer;

    /// <summary>
    /// Plays a short host sound (the trophy notification) on its own OpenAL source, mixed over the game without touching
    /// the guest's audio streams; a new effect replaces the one still playing. Any thread; never blocks.
    /// </summary>
    public void PlayEffect(VitaSharp.Core.Shell.PcmSound pcm, float gain = 1f)
    {
        if (pcm.Samples.Length == 0 || pcm.Channels is < 1 or > 2) return;
        lock (_lock)
        {
            _alc.MakeContextCurrent(_context);
            if (_fxSource == 0) _fxSource = _al.GenSource();
            else
            {
                _al.SourceStop(_fxSource);
                _al.SetSourceProperty(_fxSource, SourceInteger.Buffer, 0);
            }
            if (_fxBuffer != 0) _al.DeleteBuffer(_fxBuffer);
            _fxBuffer = _al.GenBuffer();
            fixed (short* p = pcm.Samples)
                _al.BufferData(_fxBuffer, pcm.Channels == 1 ? BufferFormat.Mono16 : BufferFormat.Stereo16, p, pcm.Samples.Length * 2, pcm.SampleRate);
            _al.SetSourceProperty(_fxSource, SourceInteger.Buffer, (int)_fxBuffer);
            _al.SetSourceProperty(_fxSource, SourceFloat.Gain, gain);
            _al.SourcePlay(_fxSource);
        }
    }

    sealed class Stream : IAudioStream
    {
        readonly OpenAlAudio _o;
        readonly uint _source;
        public readonly int Rate, Grain;
        /// <summary>Grains the host queue holds before Submit blocks. The guest-visible depth is capped separately in
        /// sceAudioOutGetRestSample (the Vita double-buffers).</summary>
        public readonly int Capacity;
        /// <summary>Grains buffered before playback (re)starts.</summary>
        public readonly int StartGrains;
        readonly Queue<uint> _free = new();
        int _queued, _underruns;
        bool _starved;
        float _volume = 1;

        public Stream(OpenAlAudio o, int rate, int grain)
        {
            _o = o;
            Rate = rate;
            Grain = Math.Max(1, grain);
            // one mix update of the device in this stream's samples (OpenAL resamples when the rates differ)
            long update = (long)o._updateSamples * rate / o._mixRate;
            int updateGrains = (int)((update + Grain - 1) / Grain);
            // like Vita3K's cubeb backend (enough buffers for one device callback, plus one), with another update of
            // headroom because OpenAL only reclaims buffers when an update runs; never fewer than 4 grains
            Capacity = Math.Max(4, 2 * updateGrains + 2);
            StartGrains = Math.Min(Capacity, updateGrains + 1);
            _source = o._al.GenSource();
            for (int i = 0; i < Capacity; i++) _free.Enqueue(o._al.GenBuffer());
        }

        public int Underruns => Volatile.Read(ref _underruns);

        public float Volume
        {
            get => _volume;
            set { _volume = value; lock (_o._lock) _o._al.SetSourceProperty(_source, SourceFloat.Gain, value); }
        }

        void Reclaim()
        {
            _o._al.GetSourceProperty(_source, GetSourceInteger.BuffersProcessed, out int processed);
            while (processed-- > 0)
            {
                uint b = 0;
                _o._al.SourceUnqueueBuffers(_source, 1, &b);
                _free.Enqueue(b);
                _queued--;
            }
        }

        /// <summary>Called under the backend's lock with its context current.</summary>
        internal void SetPaused(bool paused)
        {
            _o._al.GetSourceProperty(_source, GetSourceInteger.SourceState, out int state);
            if (paused) { if (state == (int)SourceState.Playing) _o._al.SourcePause(_source); }
            else if (state == (int)SourceState.Paused || (state == (int)SourceState.Initial && _queued >= StartGrains)) _o._al.SourcePlay(_source);
        }

        public void Submit(ReadOnlySpan<short> interleaved)
        {
            long waitStart = 0;
            while (true)
            {
                if (_o._paused)
                {
                    // a grain submitted as the pause began waits for the resume (or for the emulator to stop)
                    if (VitaSharp.Core.Kernel.GuestThread.Current?.Core.StopRequested == true) return;
                    Thread.Sleep(5);
                    continue;
                }
                lock (_o._lock)
                {
                    _o._alc.MakeContextCurrent(_o._context);
                    // a queue that stays full for 250 ms: the device stopped consuming (lost, or moved by the system).
                    // A DualShock 4 (a headset output of its own) plugged in and out several times left the output
                    // stalled: no buffer came back, sceAudioOutOutput blocked for good and Dead Space went silent
                    long now = Environment.TickCount64;
                    if (waitStart != 0 && now - waitStart > 1000)
                    {
                        // never hold the guest's audio thread: this grain is dropped
                        Log.WarnOnce("al-stalled", "Audio: the output device stopped playing; grains are dropped until it recovers");
                        Interlocked.Increment(ref _underruns);
                        return;
                    }
                    Reclaim();
                    _o._al.GetSourceProperty(_source, GetSourceInteger.SourceState, out int state);
                    // Stopped (not Initial): the queue ran dry, the guest did not submit in time (audible gap). Counted
                    // once; playback resumes after re-buffering StartGrains, so one late grain is not followed by a
                    // cascade of one-grain restarts
                    if (state == (int)SourceState.Stopped)
                    {
                        if (!_starved)
                        {
                            _starved = true;
                            Interlocked.Increment(ref _underruns);
                            Interlocked.Increment(ref TotalUnderruns);
                        }
                        // a stopped source reports every queued buffer as processed, so grains queued while
                        // re-buffering would be reclaimed unplayed (and Submit would stop blocking): take back the
                        // played buffers, then rewind to Initial, where newly queued buffers wait for SourcePlay
                        Reclaim();
                        _o._al.SourceRewind(_source);
                        state = (int)SourceState.Initial;
                    }
                    if (_queued < Capacity && _free.Count > 0)
                    {
                        uint b = _free.Dequeue();
                        fixed (short* p = interleaved)
                            _o._al.BufferData(b, BufferFormat.Stereo16, p, interleaved.Length * 2, Rate);
                        _o._al.SourceQueueBuffers(_source, 1, &b);
                        _queued++;
                        if (state != (int)SourceState.Playing && state != (int)SourceState.Paused && _queued >= StartGrains && !_o._paused)
                        {
                            _o._al.SourcePlay(_source);
                            _starved = false;
                        }
                        return;
                    }
                }
                if (waitStart == 0) waitStart = Environment.TickCount64;
                Thread.Sleep(1); // 1 ms system timer while a game runs (Runner: timeBeginPeriod)
            }
        }

        public int PendingSamples
        {
            get
            {
                lock (_o._lock)
                {
                    _o._alc.MakeContextCurrent(_o._context);
                    Reclaim();
                    _o._al.GetSourceProperty(_source, GetSourceInteger.SampleOffset, out int played);
                    return Math.Max(0, _queued * Grain - played);
                }
            }
        }

        public void Dispose()
        {
            lock (_o._lock)
            {
                _o._streams.Remove(this);
                _o._alc.MakeContextCurrent(_o._context);
                _o._al.SourceStop(_source);
                Reclaim();
                _o._al.DeleteSource(_source);
                foreach (var b in _free) _o._al.DeleteBuffer(b);
            }
        }
    }

    // ---------------------------------------------------------------- output changes (AudioOutputFollower)

    const int ALC_CONNECTED = 0x313;
    readonly AudioOutputFollower _follower;
    readonly ManualResetEventSlim _stopWatch = new();
    Thread? _watcher;
    delegate* unmanaged[Cdecl]<Device*, byte*, int*, byte> _reopen;

    /// <summary>The name of the open output (as OpenAL lists it).</summary>
    public string DeviceName => _deviceName;

    /// <summary>Checks the outputs every 2 s: a chosen output that disappears falls back to the system default and comes
    /// back when it is listed again; with the system default, a default change moves the stream. alcReopenDeviceSOFT
    /// (ALC_SOFT_reopen_device, OpenAL Soft 1.22+) keeps the context, sources and queued grains.</summary>
    void StartWatching()
    {
        if (_alc.IsExtensionPresent(_device, "ALC_SOFT_reopen_device"))
            _reopen = (delegate* unmanaged[Cdecl]<Device*, byte*, int*, byte>)_alc.GetProcAddress(_device, "alcReopenDeviceSOFT");
        if (_reopen == null) { Log.Warn("Audio: OpenAL has no ALC_SOFT_reopen_device; the output will not follow device changes"); return; }
        _watcher = new Thread(Watch) { IsBackground = true, Name = "audio output watch", Priority = ThreadPriority.BelowNormal };
        _watcher.Start();
    }

    void Watch()
    {
        while (!_stopWatch.Wait(2000))
        {
            try
            {
                var names = ListDevices(_alc, out var def);
                int connected = 1;
                lock (_lock) { if (_alc.IsExtensionPresent(_device, "ALC_EXT_disconnect")) _alc.GetContextProperty(_device, (GetContextInteger)ALC_CONNECTED, 1, &connected); }
                var next = _follower.Check(names, def, connected != 0, out var message);
                if (message != null) Log.Info(message);
                if (next != null) Reopen(next);
            }
            catch (Exception e) { Log.Warn($"Audio: output check failed: {e.Message}"); }
        }
    }

    void Reopen(string name)
    {
        lock (_lock)
        {
            var bytes = System.Text.Encoding.UTF8.GetBytes(name + "\0");
            byte ok;
            fixed (byte* p = bytes) ok = _reopen(_device, name.Length == 0 ? null : p, null);
            if (ok == 0) { Log.Warn($"Audio: could not reopen on '{name}'; staying on '{_deviceName}'"); return; }
            _deviceName = OpenedName(_alc, _device) ?? name;
            Log.Info($"Audio: now playing on '{_deviceName}'");
        }
    }

    public void Dispose()
    {
        _stopWatch.Set();
        _watcher?.Join(3000);
        if (AudioDiagnostics.DeviceInfo == Describe) AudioDiagnostics.DeviceInfo = null;
        lock (_lock)
        {
            _alc.MakeContextCurrent(_context);
            if (_fxSource != 0) { _al.SourceStop(_fxSource); _al.DeleteSource(_fxSource); _fxSource = 0; }
            if (_fxBuffer != 0) { _al.DeleteBuffer(_fxBuffer); _fxBuffer = 0; }
        }
        _alc.MakeContextCurrent(null);
        _alc.DestroyContext(_context);
        _alc.CloseDevice(_device);
    }
}
