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
    readonly string _deviceName;
    /// <summary>Mixer rate and samples per mix update of the device.</summary>
    readonly int _mixRate, _updateSamples;
    readonly List<Stream> _streams = new();

    OpenAlAudio(AL al, ALContext alc, Device* device, Context* context)
    {
        _al = al; _alc = alc; _device = device; _context = context;
        try { _deviceName = alc.GetContextProperty(device, GetContextString.DeviceSpecifier) ?? "?"; } catch { _deviceName = "?"; }
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

    static readonly string[] ControllerMarkers = ["controller", "dualsense", "dualshock", "wireless controller", "xbox"];

    static bool IsController(string name) => ControllerMarkers.Any(m => name.Contains(m, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// The device to open: <paramref name="requested"/> (--audio-device / VITASHARP_AUDIO_DEVICE) when it names one,
    /// else the system default unless it is a game controller, in which case the first speaker or headphone output.
    /// "" = OpenAL's own default.
    /// </summary>
    static string PickDevice(ALContext alc, string? requested)
    {
        var names = ListDevices(alc, out var def);
        if (!string.IsNullOrWhiteSpace(requested))
        {
            var match = names.FirstOrDefault(n => n.Equals(requested, StringComparison.OrdinalIgnoreCase))
                        ?? names.FirstOrDefault(n => n.Contains(requested, StringComparison.OrdinalIgnoreCase));
            if (match != null) return match;
            Log.Warn($"Audio: device '{requested}' not found (available: {string.Join(", ", names)}); using the default");
        }
        if (def != null && IsController(def))
        {
            var other = names.FirstOrDefault(n => !IsController(n));
            if (other != null)
            {
                Log.Info($"Audio: the default output '{def}' is a game controller; using '{other}' instead");
                return other;
            }
        }
        Log.Info($"Audio: outputs: {string.Join(" | ", names)}; system default: {def ?? "?"}");
        return def ?? "";
    }

    public static OpenAlAudio? TryCreate(string? requestedDevice = null)
    {
        try
        {
            var alc = ALContext.GetApi(true);
            var al = AL.GetApi(true);
            string name = PickDevice(alc, requestedDevice ?? Environment.GetEnvironmentVariable(EnvVars.AudioDevice));
            var dev = alc.OpenDevice(name);
            if (dev == null && name.Length > 0) dev = alc.OpenDevice("");
            if (dev == null) { Log.Warn("OpenAL: no output device"); return null; }
            var ctx = alc.CreateContext(dev, null);
            alc.MakeContextCurrent(ctx);
            var a = new OpenAlAudio(al, alc, dev, ctx);
            Log.Info($"Audio: OpenAL Soft, {a.Describe()}");
            AudioDiagnostics.DeviceInfo = a.Describe;
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

        public void Submit(ReadOnlySpan<short> interleaved)
        {
            while (true)
            {
                lock (_o._lock)
                {
                    _o._alc.MakeContextCurrent(_o._context);
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
                        if (state != (int)SourceState.Playing && _queued >= StartGrains)
                        {
                            _o._al.SourcePlay(_source);
                            _starved = false;
                        }
                        return;
                    }
                }
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

    public void Dispose()
    {
        if (AudioDiagnostics.DeviceInfo == Describe) AudioDiagnostics.DeviceInfo = null;
        _alc.MakeContextCurrent(null);
        _alc.DestroyContext(_context);
        _alc.CloseDevice(_device);
    }
}
