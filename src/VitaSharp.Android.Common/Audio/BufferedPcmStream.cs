using System.Diagnostics;
using VitaSharp.Core.Host;
using VitaSharp.Core.Util;

namespace VitaSharp.Android.Audio;

/// <summary>Result codes of <see cref="IPcmSink.Write"/> below zero.</summary>
public static class PcmSinkStatus
{
    /// <summary>The output device went away (headphones unplugged, Bluetooth switched, route change): reopen.</summary>
    public const int Disconnected = -1;
    /// <summary>Any other failure.</summary>
    public const int Error = -2;
}

/// <summary>
/// One open device output stream, signed 16-bit interleaved: the Android app's AAudio and AudioTrack outputs
/// (src/VitaSharp.Android/Audio). Platform-neutral so <see cref="BufferedPcmStream"/>, which owns all the buffering
/// policy, is unit-tested on the desktop with a fake sink.
/// </summary>
public interface IPcmSink : IDisposable
{
    /// <summary>The rate the device runs this stream at (the device's native rate, not necessarily the guest's).</summary>
    int SampleRate { get; }
    int Channels { get; }
    /// <summary>Frames the device consumes per period (AAudio framesPerBurst, AudioManager FRAMES_PER_BUFFER).</summary>
    int FramesPerBurst { get; }
    /// <summary>"AAudio low-latency exclusive" etc. (logs, AUDIO summary).</summary>
    string Description { get; }
    /// <summary>Sets how many frames may be queued before <see cref="Write"/> blocks; returns the size the device took.</summary>
    int SetBufferSize(int frames);
    /// <summary>Writes up to all frames, waiting at most <paramref name="timeoutNanos"/> for room (0 = no wait). Returns
    /// the frames written, or a negative <see cref="PcmSinkStatus"/>.</summary>
    int Write(ReadOnlySpan<short> interleaved, long timeoutNanos);
    long FramesWritten { get; }
    long FramesRead { get; }
    /// <summary>Times the device ran out of data (AAudio getXRunCount, AudioTrack getUnderrunCount).</summary>
    int XRunCount { get; }
    /// <summary>Set asynchronously (AAudio error callback) when the device was lost.</summary>
    bool Disconnected { get; }
    void Start();
    void Pause();
}

/// <summary>Opens a device stream for <paramref name="channels"/> channels; null when no output is available.</summary>
public delegate IPcmSink? PcmSinkFactory(int channels);

/// <summary>
/// The buffering policy of the desktop OpenAL backend (src/VitaSharp.Desktop/Host/OpenAlAudio.cs), applied to a
/// device whose period is <c>FramesPerBurst</c>: up to <see cref="Capacity"/> grains queued before the guest blocks
/// (two device periods plus two grains, never fewer than four), playback (re)starting once <see cref="StartGrains"/>
/// (one period plus a grain) are buffered.
/// </summary>
public readonly record struct PcmBufferPolicy(int Capacity, int StartGrains, int BurstGrains)
{
    public static PcmBufferPolicy For(int grain, int guestRate, int deviceRate, int burstFrames)
    {
        grain = Math.Max(1, grain);
        // one device period in guest samples (the stream is resampled when the rates differ)
        long burst = deviceRate > 0 ? (long)Math.Max(1, burstFrames) * guestRate / deviceRate : burstFrames;
        int burstGrains = (int)Math.Max(1, (burst + grain - 1) / grain);
        int capacity = Math.Max(4, 2 * burstGrains + 2);
        return new PcmBufferPolicy(capacity, Math.Min(capacity, burstGrains + 1), burstGrains);
    }
}

/// <summary>
/// A guest audio port's host stream on Android (<see cref="IAudioStream"/>), over an <see cref="IPcmSink"/>:
/// <list type="bullet">
/// <item>buffering as on the desktop (<see cref="PcmBufferPolicy"/>): the device buffer is sized to the capacity, the
///   first grains are written before the stream starts, Submit blocks while the buffer is full (this paces the guest
///   like the Vita's output does);</item>
/// <item>underruns: a dry spell is counted once (the device's xrun counter moved since the previous grain), like the
///   OpenAL source stopping; when the buffer is completely empty the next grain is preceded by silence up to the start
///   threshold, as OpenAL re-buffers before playing again, so one late grain is not followed by a cascade of glitches;</item>
/// <item>the device's native rate: the sink runs at the rate the device reports and the guest's 48 kHz is resampled
///   (<see cref="LinearResampler"/>) when they differ, which keeps AAudio on its low-latency path;</item>
/// <item>device changes: a disconnected sink (headphones, Bluetooth, USB) is closed and a new one opened on the next
///   grain, on the guest's thread (never in the device callback); without a device the guest is paced by the clock.</item>
/// </list>
/// Thread use: Submit from the port's guest thread; Pause/Resume from the activity; PendingSamples from any thread.
/// </summary>
public sealed class BufferedPcmStream : IAudioStream
{
    const long WriteTimeoutNanos = 200_000_000;
    const double ReopenRetrySeconds = 1.0;

    readonly PcmSinkFactory _factory;
    readonly Action<BufferedPcmStream>? _closed;
    readonly object _lock = new();
    readonly LinearResampler _resampler;
    IPcmSink? _sink;
    PcmBufferPolicy _policy;
    short[] _scaled = [], _resampled = [], _silence = [];
    bool _started, _paused, _disposed, _reopen;
    int _xrunsSeen, _underruns, _reopens;
    long _primedFrames;
    double _nextReopenAt;
    // pacing while no device is open
    readonly Stopwatch _clock = Stopwatch.StartNew();
    double _paceAt;

    public int Rate { get; }
    public int Channels { get; }
    public int Grain { get; }

    public BufferedPcmStream(PcmSinkFactory factory, int rate, int channels, int grain, Action<BufferedPcmStream>? closed = null)
    {
        _factory = factory;
        _closed = closed;
        Rate = rate;
        Channels = Math.Max(1, channels);
        Grain = Math.Max(1, grain);
        _resampler = new LinearResampler(Channels);
        lock (_lock) Open();
    }

    public float Volume { get; set; } = 1;
    public int Underruns => Volatile.Read(ref _underruns);
    /// <summary>Times the device stream was reopened (device change or disconnect).</summary>
    public int Reopens => Volatile.Read(ref _reopens);
    public PcmBufferPolicy Policy => _policy;
    public IPcmSink? Sink => _sink;

    void Open()
    {
        _sink = null;
        IPcmSink? s;
        try { s = _factory(Channels); }
        catch (Exception e) { Log.Warn($"Audio: opening the output failed: {e.Message}"); s = null; }
        if (s == null)
        {
            _nextReopenAt = _clock.Elapsed.TotalSeconds + ReopenRetrySeconds;
            Log.WarnOnce("android-audio-none", "Audio: no output device; the guest is paced by the clock until one comes back");
            return;
        }
        _policy = PcmBufferPolicy.For(Grain, Rate, s.SampleRate, s.FramesPerBurst);
        long deviceGrain = (long)Grain * s.SampleRate / Rate;
        int asked = (int)Math.Min(int.MaxValue, _policy.Capacity * Math.Max(1, deviceGrain));
        int got = s.SetBufferSize(asked);
        if (got > 0 && got < asked)
            Log.Info($"Audio: the device holds {got} frames, not {asked}: the stream queues less than {_policy.Capacity} grains");
        _resampler.Reset(Rate, s.SampleRate);
        _xrunsSeen = s.XRunCount;
        _started = false;
        _primedFrames = 0;
        _sink = s;
        Log.Info($"Audio: {Describe()}");
    }

    void Close()
    {
        var s = _sink;
        _sink = null;
        if (s == null) return;
        try { s.Pause(); } catch (Exception) { }
        try { s.Dispose(); } catch (Exception e) { Log.Warn($"Audio: closing the output: {e.Message}"); }
    }

    /// <summary>The device configuration changed (AudioDeviceCallback): reopen at the next grain.</summary>
    public void RequestReopen() => Volatile.Write(ref _reopen, true);

    public string Describe()
    {
        var s = _sink;
        if (s == null) return $"stream {Rate} Hz grain {Grain}: no output device, paced by the clock, underruns {Underruns}";
        double grainMs = Grain * 1000.0 / Rate;
        return $"{s.Description} {s.SampleRate} Hz{(s.SampleRate != Rate ? $" (resampled from {Rate})" : "")}, burst {s.FramesPerBurst} frames " +
               $"({s.FramesPerBurst * 1000.0 / s.SampleRate:F1} ms); stream grain {Grain}: up to {_policy.Capacity} grains queued " +
               $"({_policy.Capacity * grainMs:F1} ms), starts at {_policy.StartGrains}, latency ~{_policy.StartGrains * grainMs + s.FramesPerBurst * 1000.0 / s.SampleRate:F0} ms, " +
               $"underruns {Underruns}{(Reopens > 0 ? $", reopened {Reopens}x" : "")}";
    }

    public void Submit(ReadOnlySpan<short> interleaved)
    {
        if (interleaved.IsEmpty) return;
        lock (_lock)
        {
            if (_disposed) return;
            if (_sink is { } cur && (cur.Disconnected || Volatile.Read(ref _reopen))) Reopen(cur.Disconnected ? "the output device was disconnected" : "the output device changed");
            if (_sink == null && _clock.Elapsed.TotalSeconds >= _nextReopenAt) Open();
            if (_sink == null) { Pace(interleaved.Length / Channels); return; }

            var data = Prepare(interleaved);
            for (int attempt = 0; attempt < 2; attempt++)
            {
                int r = WriteGrain(data);
                if (r >= 0) return;
                if (r != PcmSinkStatus.Disconnected) { Log.WarnOnce("android-audio-write", $"Audio: write failed ({r})"); Pace(interleaved.Length / Channels); return; }
                Reopen("the output device was disconnected");
                if (_sink == null) { Pace(interleaved.Length / Channels); return; }
                data = Prepare(interleaved);
            }
        }
    }

    void Reopen(string why)
    {
        Volatile.Write(ref _reopen, false);
        Close();
        Interlocked.Increment(ref _reopens);
        Log.Info($"Audio: {why}: reopening the output");
        Open();
        if (_sink != null && _paused) _sink.Pause();
    }

    /// <summary>Volume, then the device rate.</summary>
    ReadOnlySpan<short> Prepare(ReadOnlySpan<short> data)
    {
        float vol = Volume;
        if (vol != 1)
        {
            if (_scaled.Length < data.Length) _scaled = new short[data.Length];
            for (int i = 0; i < data.Length; i++) _scaled[i] = (short)Math.Clamp((int)(data[i] * vol), short.MinValue, short.MaxValue);
            data = _scaled.AsSpan(0, data.Length);
        }
        if (!_resampler.Active) return data;
        int need = _resampler.MaxOutputFrames(data.Length / Channels) * Channels;
        if (_resampled.Length < need) _resampled = new short[need];
        int frames = _resampler.Process(data, _resampled);
        return _resampled.AsSpan(0, frames * Channels);
    }

    int WriteGrain(ReadOnlySpan<short> data)
    {
        var s = _sink!;
        int frames = data.Length / Channels;
        if (_started)
        {
            int x = s.XRunCount;
            if (x != _xrunsSeen)
            {
                // the device played silence since the previous grain: one dry spell, counted once
                _xrunsSeen = x;
                Interlocked.Increment(ref _underruns);
            }
            // completely drained: re-buffer to the start threshold first (silence), as OpenAL does before replaying
            if (s.FramesWritten - s.FramesRead <= 0 && _policy.StartGrains > 1)
            {
                int pad = (int)((long)(_policy.StartGrains - 1) * Grain * s.SampleRate / Rate);
                int r = WriteSilence(s, pad);
                if (r < 0) return r;
            }
        }
        int done = 0;
        while (done < frames)
        {
            // before the start the buffer has room for every primed grain (capacity >= start threshold): no wait
            int n = s.Write(data[(done * Channels)..], _started ? WriteTimeoutNanos : 0);
            if (n < 0) return n;
            done += n;
            if (n > 0) continue;
            if (!_started) { Start(s); continue; }
            // full: wait for room; while the activity is paused the device does not drain, so wait for Resume
            while (_paused && !_disposed) Monitor.Wait(_lock, 100);
            if (_disposed || s.Disconnected) return s.Disconnected ? PcmSinkStatus.Disconnected : 0;
        }
        if (!_started)
        {
            _primedFrames += frames;
            if (_primedFrames >= (long)_policy.StartGrains * Grain * s.SampleRate / Rate) Start(s);
        }
        return done;
    }

    void Start(IPcmSink s)
    {
        _started = true;
        _xrunsSeen = s.XRunCount;
        if (!_paused) s.Start();
    }

    int WriteSilence(IPcmSink s, int frames)
    {
        int n = frames * Channels;
        if (_silence.Length < n) _silence = new short[n];
        return s.Write(_silence.AsSpan(0, n), 0);
    }

    /// <summary>No device: sleep for the grain's duration so the guest keeps its pace.</summary>
    void Pace(int frames)
    {
        double now = _clock.Elapsed.TotalSeconds;
        if (_paceAt < now - 0.1) _paceAt = now;
        _paceAt += (double)frames / Rate;
        double wait = _paceAt - now;
        if (wait > 0) Monitor.Wait(_lock, TimeSpan.FromSeconds(Math.Min(wait, 0.5)));
    }

    public int PendingSamples
    {
        get
        {
            var s = _sink;
            if (s == null) return 0;
            long q = Math.Max(0, s.FramesWritten - s.FramesRead);
            return (int)Math.Min(int.MaxValue, q * Rate / Math.Max(1, s.SampleRate));
        }
    }

    /// <summary>Activity onPause: the device stops consuming; a Submit waiting for room waits for Resume.</summary>
    public void Pause()
    {
        _paused = true;
        try { _sink?.Pause(); } catch (Exception) { }
    }

    public void Resume()
    {
        _paused = false;
        try { if (_started) _sink?.Start(); } catch (Exception) { }
        lock (_lock) Monitor.PulseAll(_lock);
    }

    public void Dispose()
    {
        _disposed = true;
        lock (_lock)
        {
            Monitor.PulseAll(_lock);
            Close();
        }
        _closed?.Invoke(this);
    }
}

/// <summary>
/// Linear-interpolation sample-rate converter for interleaved 16-bit audio, continuous across calls (the guest's
/// 48 kHz grains to a 44.1 kHz device, for example). Only used when the device's native rate differs from the guest's.
/// </summary>
public sealed class LinearResampler(int channels)
{
    readonly int _channels = Math.Max(1, channels);
    short[] _last = new short[Math.Max(1, channels)];
    bool _haveLast;
    double _pos, _step = 1;

    public bool Active { get; private set; }

    public void Reset(int inRate, int outRate)
    {
        Active = inRate > 0 && outRate > 0 && inRate != outRate;
        _step = Active ? (double)inRate / outRate : 1;
        _pos = 0;
        _haveLast = false;
        _last = new short[_channels];
    }

    public int MaxOutputFrames(int inputFrames) => (int)Math.Ceiling((inputFrames + 1) / _step) + 1;

    /// <summary>Resamples <paramref name="input"/> into <paramref name="output"/>; returns the output frames.</summary>
    public int Process(ReadOnlySpan<short> input, Span<short> output)
    {
        int ch = _channels, frames = input.Length / ch;
        if (frames == 0) return 0;
        // the frames of this call, preceded by the previous call's last one (index 0) when there is one;
        // _pos is the next output position on that index scale
        int off = _haveLast ? 1 : 0, n = frames + off, o = 0;
        while (true)
        {
            int i0 = (int)_pos;
            if (i0 + 1 >= n) break; // needs a frame of the next call
            double t = _pos - i0;
            for (int c = 0; c < ch; c++)
            {
                int a = i0 < off ? _last[c] : input[(i0 - off) * ch + c];
                int b = input[(i0 + 1 - off) * ch + c];
                output[o * ch + c] = (short)Math.Round(a + (b - a) * t);
            }
            o++;
            _pos += _step;
        }
        for (int c = 0; c < ch; c++) _last[c] = input[(frames - 1) * ch + c];
        _haveLast = true;
        // the last frame becomes index 0 of the next call
        _pos -= n - 1;
        return o;
    }
}
