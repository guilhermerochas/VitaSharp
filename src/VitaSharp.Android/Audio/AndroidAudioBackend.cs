using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Android.Content;
using Android.Media;
using Android.OS;
using VitaSharp.Core.Host;
using VitaSharp.Core.Util;

namespace VitaSharp.Android.Audio;

/// <summary>
/// Built into the app, not run on a device yet. Audio output behind the core's <see cref="IAudioBackend"/> (the
/// interface the desktop's OpenAL backend implements): one device stream per guest audio port, through the NDK AAudio
/// API on Android 8.1+ (API 27; Oboe's rule, 8.0's AAudio has known bugs) and the Java AudioTrack in low-latency mode
/// otherwise or when AAudio cannot open. The buffering policy, the device-rate resampling, the underrun accounting and
/// the reopen after a device change are the platform-neutral <see cref="BufferedPcmStream"/> (unit-tested on the
/// desktop); this file only opens the device streams:
/// <list type="bullet">
/// <item>AAudio: low-latency performance mode, exclusive sharing when the device grants it (MMAP, else shared), usage
///   GAME, 16-bit, at the device's native rate (AudioManager PROPERTY_OUTPUT_SAMPLE_RATE) so AAudio does not insert its
///   own resampler (which disables the fast path); the error callback flags a disconnect, the guest thread reopens;</item>
/// <item>AudioDeviceCallback: an output added or removed (headphones, Bluetooth, USB) makes every stream reopen on its
///   next grain, at the new device's rate.</item>
/// </list>
/// EmulationSession installs it, pauses and resumes it with the game activity.
/// </summary>
public sealed class AndroidAudioBackend : IAudioBackend
{
    readonly AudioManager? _manager;
    readonly bool _useAAudio;
    readonly DeviceCallback? _callback;
    readonly List<BufferedPcmStream> _streams = new();

    AndroidAudioBackend(Context context, bool aaudio)
    {
        _manager = context.GetSystemService(Context.AudioService) as AudioManager;
        _useAAudio = aaudio;
        if (_manager != null && Looper.MainLooper is { } looper)
        {
            _callback = new DeviceCallback(this);
            _manager.RegisterAudioDeviceCallback(_callback, new Handler(looper));
        }
    }

    public static AndroidAudioBackend? TryCreate(Context context)
    {
        bool aaudio = OperatingSystem.IsAndroidVersionAtLeast(27) && AAudioSink.Available();
        var b = new AndroidAudioBackend(context, aaudio);
        Log.Info($"Audio: {(aaudio ? "AAudio" : "AudioTrack")}, device {b.DeviceRate} Hz, {b.DeviceBurst} frames per buffer");
        AudioDiagnostics.DeviceInfo = b.Describe;
        return b;
    }

    /// <summary>The primary output's native rate as the device reports it.</summary>
    public int DeviceRate => int.TryParse(_manager?.GetProperty(AudioManager.PropertyOutputSampleRate), out int r) && r > 0 ? r : 48000;

    /// <summary>The primary output's period (the burst size AudioTrack's fast path uses).</summary>
    public int DeviceBurst => int.TryParse(_manager?.GetProperty(AudioManager.PropertyOutputFramesPerBuffer), out int f) && f > 0 ? f : 256;

    IPcmSink? OpenSink(int channels)
    {
        int rate = DeviceRate;
        if (_useAAudio && AAudioSink.Open(channels, rate) is { } a) return a;
        return AudioTrackSink.Open(channels, rate, DeviceBurst);
    }

    public IAudioStream OpenStream(int sampleRate, int channels, int grainSamples)
    {
        var s = new BufferedPcmStream(OpenSink, sampleRate, channels, grainSamples, Closed);
        lock (_streams) _streams.Add(s);
        return s;
    }

    void Closed(BufferedPcmStream s)
    {
        lock (_streams) _streams.Remove(s);
    }

    BufferedPcmStream[] Streams()
    {
        lock (_streams) return [.. _streams];
    }

    /// <summary>Device and per-stream buffering (AUDIO summary, F10-style audio.txt, debug lines).</summary>
    public string Describe()
    {
        var all = Streams();
        string head = $"Android API {(int)Build.VERSION.SdkInt} {(_useAAudio ? "AAudio" : "AudioTrack")}, device {DeviceRate} Hz, {DeviceBurst} frames per buffer";
        return all.Length == 0 ? head : head + "; " + string.Join("; ", all.Select(s => s.Describe()));
    }

    int _effectBusy;

    /// <summary>
    /// Plays a short host sound (the trophy chime) on a device stream of its own (AAudio, else AudioTrack), mixed by
    /// the system over the game's streams, which are never touched. Returns at once; a sound still playing makes a new
    /// one be skipped.
    /// </summary>
    public void PlayEffect(VitaSharp.Core.Shell.PcmSound pcm, float gain = 1f)
    {
        if (Interlocked.Exchange(ref _effectBusy, 1) == 1) return;
        new Thread(() =>
        {
            try
            {
                using var sink = OpenSink(Math.Clamp(pcm.Channels, 1, 2));
                if (sink != null) EffectPlayer.Play(sink, pcm, gain);
            }
            catch (Exception e) { Log.WarnOnce("effect-play", $"Audio: sound effect failed: {e.Message}"); }
            finally { Volatile.Write(ref _effectBusy, 0); }
        }) { IsBackground = true, Name = "Sound effect" }.Start();
    }

    /// <summary>Activity lifecycle: pauses every stream (onPause) so the device stops playing the queued grains.</summary>
    public void Pause()
    {
        foreach (var s in Streams()) s.Pause();
    }

    /// <summary>Restarts the streams paused by <see cref="Pause"/> (onResume).</summary>
    public void Resume()
    {
        foreach (var s in Streams()) s.Resume();
    }

    /// <summary>The emulation was paused or resumed (EmulationPause: the pause menu, the activity's lifecycle).</summary>
    public void SetPaused(bool paused)
    {
        if (paused) Pause(); else Resume();
    }

    void DevicesChanged(string what)
    {
        Log.Info($"Audio: {what}: the streams reopen on their next grain");
        foreach (var s in Streams()) s.RequestReopen();
    }

    public void Dispose()
    {
        if (_callback != null) _manager?.UnregisterAudioDeviceCallback(_callback);
        foreach (var s in Streams()) s.Dispose();
        if (AudioDiagnostics.DeviceInfo == Describe) AudioDiagnostics.DeviceInfo = null;
    }

    sealed class DeviceCallback(AndroidAudioBackend owner) : AudioDeviceCallback
    {
        // registering reports every current device as "added" once: not a change
        bool _initial = true;

        public override void OnAudioDevicesAdded(AudioDeviceInfo[]? addedDevices)
        {
            var sinks = Sinks(addedDevices);
            if (_initial) { _initial = false; Log.Info($"Audio: outputs: {sinks}"); return; }
            if (sinks.Length > 0) owner.DevicesChanged($"output added ({sinks})");
        }

        public override void OnAudioDevicesRemoved(AudioDeviceInfo[]? removedDevices)
        {
            var sinks = Sinks(removedDevices);
            if (sinks.Length > 0) owner.DevicesChanged($"output removed ({sinks})");
        }

        static string Sinks(AudioDeviceInfo[]? devices) =>
            string.Join(", ", (devices ?? []).Where(d => d.IsSink).Select(d => $"{d.Type} '{d.ProductName}'"));
    }
}

/// <summary>An AAudio output stream (NDK aaudio/AAudio.h, blocking writes).</summary>
sealed unsafe class AAudioSink : IPcmSink
{
    nint _stream;
    GCHandle _self;
    volatile bool _disconnected;

    public int SampleRate { get; }
    public int Channels { get; }
    public int FramesPerBurst { get; }
    public string Description { get; }

    AAudioSink(nint stream, int channels)
    {
        _stream = stream;
        Channels = channels;
        SampleRate = AAudio.AAudioStream_getSampleRate(stream);
        FramesPerBurst = Math.Max(1, AAudio.AAudioStream_getFramesPerBurst(stream));
        bool exclusive = AAudio.AAudioStream_getSharingMode(stream) == AAudio.SharingModeExclusive;
        bool lowLatency = AAudio.AAudioStream_getPerformanceMode(stream) == AAudio.PerformanceModeLowLatency;
        Description = $"AAudio {(lowLatency ? "low-latency" : "normal")} {(exclusive ? "exclusive" : "shared")}, capacity {AAudio.AAudioStream_getBufferCapacityInFrames(stream)} frames";
    }

    public static bool Available()
    {
        try
        {
            if (AAudio.AAudio_createStreamBuilder(out nint b) != 0) return false;
            AAudio.AAudioStreamBuilder_delete(b);
            return true;
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException)
        {
            Log.Warn($"AAudio unavailable: {e.Message}");
            return false;
        }
    }

    public static AAudioSink? Open(int channels, int deviceRate)
    {
        if (AAudio.AAudio_createStreamBuilder(out nint b) != 0) return null;
        var holder = new AAudioSink[1];
        var handle = GCHandle.Alloc(holder);
        nint stream = 0;
        int r;
        try
        {
            AAudio.AAudioStreamBuilder_setFormat(b, AAudio.FormatPcmI16);
            AAudio.AAudioStreamBuilder_setChannelCount(b, channels);
            // the rate the device reports: AAudio runs at it without a resampler of its own
            AAudio.AAudioStreamBuilder_setSampleRate(b, deviceRate);
            AAudio.AAudioStreamBuilder_setPerformanceMode(b, AAudio.PerformanceModeLowLatency);
            // MMAP when the device offers it; AAudio falls back to shared on its own
            AAudio.AAudioStreamBuilder_setSharingMode(b, AAudio.SharingModeExclusive);
            // the capacity is AAudio's own unless VITASHARP_AAUDIO_CAPACITY asks for one: requesting 8192 frames gave the
            // Redmi Note 14 a 4096-frame burst and ~181 ms of latency; AAudio's own is 4104 frames, a 2052-frame burst and
            // ~96 ms, with no underrun in Devious Dungeon (86 s) or Knightin'+
            int capacity = int.TryParse(System.Environment.GetEnvironmentVariable(VitaSharp.Core.Util.EnvVars.AAudioCapacity), out int cap) ? cap : 0;
            if (capacity > 0) AAudio.AAudioStreamBuilder_setBufferCapacityInFrames(b, capacity);
            if (OperatingSystem.IsAndroidVersionAtLeast(28)) AAudio.AAudioStreamBuilder_setUsage(b, AAudio.UsageGame);
            AAudio.AAudioStreamBuilder_setErrorCallback(b, &OnError, GCHandle.ToIntPtr(handle));
            r = AAudio.AAudioStreamBuilder_openStream(b, out stream);
        }
        finally { AAudio.AAudioStreamBuilder_delete(b); }
        if (r != 0 || stream == 0)
        {
            handle.Free();
            Log.Warn($"AAudio: openStream failed ({AAudio.ResultText(r)}), trying AudioTrack");
            return null;
        }
        var s = new AAudioSink(stream, channels) { _self = handle };
        holder[0] = s;
        return s;
    }

    /// <summary>AAudio's error callback (its own thread): only flags the stream; the guest thread reopens it, because a
    /// stream must not be closed or reopened from inside its callback.</summary>
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    static void OnError(nint stream, nint user, int error)
    {
        try
        {
            if (GCHandle.FromIntPtr(user).Target is AAudioSink[] { Length: 1 } h && h[0] is { } s)
            {
                s._disconnected = true;
                Log.Info($"AAudio: stream error {AAudio.ResultText(error)}");
            }
        }
        catch (Exception) { }
    }

    public bool Disconnected => _disconnected;

    public int SetBufferSize(int frames) => _stream == 0 ? 0 : AAudio.AAudioStream_setBufferSizeInFrames(_stream, frames);

    public int Write(ReadOnlySpan<short> interleaved, long timeoutNanos)
    {
#if VITASHARP_DEVELOPER
        Core.Util.Developer.DevTrace.Begin("audio grain"); Core.Util.Developer.DevTrace.End();
#endif
        if (_stream == 0) return PcmSinkStatus.Error;
        int frames = interleaved.Length / Channels;
        fixed (short* p = interleaved)
        {
            int n = AAudio.AAudioStream_write(_stream, p, frames, timeoutNanos);
            if (n >= 0) return n;
            if (n == AAudio.ErrorDisconnected) { _disconnected = true; return PcmSinkStatus.Disconnected; }
            Log.WarnOnce($"aaudio-write{n}", $"AAudio: write failed ({AAudio.ResultText(n)})");
            return PcmSinkStatus.Error;
        }
    }

    public long FramesWritten => _stream == 0 ? 0 : AAudio.AAudioStream_getFramesWritten(_stream);
    public long FramesRead => _stream == 0 ? 0 : AAudio.AAudioStream_getFramesRead(_stream);
    public int XRunCount => _stream == 0 ? 0 : AAudio.AAudioStream_getXRunCount(_stream);

    public void Start()
    {
        if (_stream != 0) AAudio.AAudioStream_requestStart(_stream);
    }

    public void Pause()
    {
        if (_stream != 0) AAudio.AAudioStream_requestPause(_stream);
    }

    public void Dispose()
    {
        if (_stream == 0) return;
        AAudio.AAudioStream_requestStop(_stream);
        AAudio.AAudioStream_close(_stream);
        _stream = 0;
        if (_self.IsAllocated) _self.Free();
    }
}

/// <summary>A Java AudioTrack output (API 26: low-latency performance mode, streaming, game usage).</summary>
sealed class AudioTrackSink : IPcmSink
{
    const int ErrorDeadObject = -6; // AudioTrack.ERROR_DEAD_OBJECT: the track died with its device
    readonly AudioTrack _track;
    short[] _buf = [];
    long _written, _read;
    int _lastHead;
    bool _disconnected;

    public int SampleRate { get; }
    public int Channels { get; }
    public int FramesPerBurst { get; }
    public string Description { get; }

    AudioTrackSink(AudioTrack track, int channels, int rate, int burst)
    {
        _track = track;
        Channels = channels;
        SampleRate = rate;
        FramesPerBurst = burst;
        bool fast = OperatingSystem.IsAndroidVersionAtLeast(26) && track.PerformanceMode == AudioTrackPerformanceMode.LowLatency;
        Description = $"AudioTrack {(fast ? "low-latency" : "normal")}";
    }

    public static AudioTrackSink? Open(int channels, int rate, int burst)
    {
        try
        {
            var mask = channels == 1 ? ChannelOut.Mono : ChannelOut.Stereo;
            int min = AudioTrack.GetMinBufferSize(rate, mask, Encoding.Pcm16bit);
            var builder = new AudioTrack.Builder()
                .SetAudioAttributes(new AudioAttributes.Builder().SetUsage(AudioUsageKind.Game)!.SetContentType(AudioContentType.Music)!.Build()!)!
                .SetAudioFormat(new AudioFormat.Builder().SetEncoding(Encoding.Pcm16bit)!.SetSampleRate(rate)!.SetChannelMask(mask)!.Build()!)!
                .SetTransferMode(AudioTrackMode.Stream)!
                .SetBufferSizeInBytes(Math.Max(min, 8192 * channels * 2))!;
            if (OperatingSystem.IsAndroidVersionAtLeast(26)) builder.SetPerformanceMode(AudioTrackPerformanceMode.LowLatency);
            var track = builder.Build();
            return new AudioTrackSink(track, channels, track.SampleRate, burst);
        }
        catch (Java.Lang.Exception e)
        {
            Log.Warn($"AudioTrack: could not open the output: {e.Message}");
            return null;
        }
    }

    public bool Disconnected => _disconnected;

    public int SetBufferSize(int frames) => _track.SetBufferSizeInFrames(frames);

    public int Write(ReadOnlySpan<short> interleaved, long timeoutNanos)
    {
#if VITASHARP_DEVELOPER
        Core.Util.Developer.DevTrace.Begin("audio grain"); Core.Util.Developer.DevTrace.End();
#endif
        if (_buf.Length < interleaved.Length) _buf = new short[interleaved.Length];
        interleaved.CopyTo(_buf);
        int n = _track.Write(_buf, 0, interleaved.Length, timeoutNanos == 0 ? WriteMode.NonBlocking : WriteMode.Blocking);
        if (n == ErrorDeadObject) { _disconnected = true; return PcmSinkStatus.Disconnected; }
        if (n < 0) { Log.WarnOnce($"audiotrack-write{n}", $"AudioTrack: write failed ({n})"); return PcmSinkStatus.Error; }
        int frames = n / Channels;
        _written += frames;
        return frames;
    }

    public long FramesWritten => _written;

    /// <summary>getPlaybackHeadPosition is a wrapping 32-bit frame counter: extended to 64 bits here.</summary>
    public long FramesRead
    {
        get
        {
            int head = _track.PlaybackHeadPosition;
            _read += (uint)(head - _lastHead);
            _lastHead = head;
            return _read;
        }
    }

    public int XRunCount => _track.UnderrunCount;

    public void Start() => _track.Play();

    public void Pause() => _track.Pause();

    public void Dispose()
    {
        try { _track.Stop(); } catch (Java.Lang.IllegalStateException) { }
        _track.Release();
        _track.Dispose();
    }
}

/// <summary>NDK AAudio API (aaudio/AAudio.h).</summary>
internal static unsafe partial class AAudio
{
    const string Lib = "libaaudio.so";
    public const int FormatPcmI16 = 1, PerformanceModeLowLatency = 12, SharingModeExclusive = 0, UsageGame = 14;
    public const int ErrorDisconnected = -899;

    public static string ResultText(int r)
    {
        try { return Marshal.PtrToStringUTF8(AAudio_convertResultToText(r)) ?? r.ToString(); }
        catch (EntryPointNotFoundException) { return r.ToString(); }
    }

    [LibraryImport(Lib)] public static partial int AAudio_createStreamBuilder(out nint builder);
    [LibraryImport(Lib)] public static partial nint AAudio_convertResultToText(int result);
    [LibraryImport(Lib)] public static partial int AAudioStreamBuilder_delete(nint builder);
    [LibraryImport(Lib)] public static partial void AAudioStreamBuilder_setFormat(nint builder, int format);
    [LibraryImport(Lib)] public static partial void AAudioStreamBuilder_setChannelCount(nint builder, int channels);
    [LibraryImport(Lib)] public static partial void AAudioStreamBuilder_setSampleRate(nint builder, int rate);
    [LibraryImport(Lib)] public static partial void AAudioStreamBuilder_setPerformanceMode(nint builder, int mode);
    [LibraryImport(Lib)] public static partial void AAudioStreamBuilder_setSharingMode(nint builder, int mode);
    [LibraryImport(Lib)] public static partial void AAudioStreamBuilder_setUsage(nint builder, int usage);
    [LibraryImport(Lib)] public static partial void AAudioStreamBuilder_setBufferCapacityInFrames(nint builder, int frames);
    [LibraryImport(Lib)] public static partial void AAudioStreamBuilder_setErrorCallback(nint builder, delegate* unmanaged[Cdecl]<nint, nint, int, void> callback, nint userData);
    [LibraryImport(Lib)] public static partial int AAudioStreamBuilder_openStream(nint builder, out nint stream);
    [LibraryImport(Lib)] public static partial int AAudioStream_setBufferSizeInFrames(nint stream, int frames);
    [LibraryImport(Lib)] public static partial int AAudioStream_getBufferCapacityInFrames(nint stream);
    [LibraryImport(Lib)] public static partial int AAudioStream_getSampleRate(nint stream);
    [LibraryImport(Lib)] public static partial int AAudioStream_getFramesPerBurst(nint stream);
    [LibraryImport(Lib)] public static partial int AAudioStream_getSharingMode(nint stream);
    [LibraryImport(Lib)] public static partial int AAudioStream_getPerformanceMode(nint stream);
    [LibraryImport(Lib)] public static partial int AAudioStream_getXRunCount(nint stream);
    [LibraryImport(Lib)] public static partial int AAudioStream_requestStart(nint stream);
    [LibraryImport(Lib)] public static partial int AAudioStream_requestStop(nint stream);
    [LibraryImport(Lib)] public static partial int AAudioStream_requestPause(nint stream);
    [LibraryImport(Lib)] public static partial int AAudioStream_close(nint stream);
    [LibraryImport(Lib)] public static partial int AAudioStream_write(nint stream, void* buffer, int numFrames, long timeoutNanoseconds);
    [LibraryImport(Lib)] public static partial long AAudioStream_getFramesWritten(nint stream);
    [LibraryImport(Lib)] public static partial long AAudioStream_getFramesRead(nint stream);
}
