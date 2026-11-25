using System.Diagnostics;

namespace VitaSharp.Core.Host;

/// <summary>
/// Host microphone provider for SceAudioIn (desktop capture device, Android AudioRecord, a scripted test tone). The core
/// asks for a stream when the guest opens its microphone port and disposes it when the port is released, so the host
/// device (and the system's "microphone in use" indicator) is only on while an application listens. Without a provider,
/// or when <see cref="Open"/> returns null (no device, permission denied), the port still works and delivers silence at
/// the grain pace, as the Vita does.
/// </summary>
public interface IMicrophoneSource : IDisposable
{
    /// <summary>Human-readable names of the capture devices the host offers (for logs and settings).</summary>
    IReadOnlyList<string> DeviceNames { get; }

    /// <summary>Starts capturing 16-bit mono at about <paramref name="sampleRate"/> Hz (the port's rate; the stream says the
    /// rate it really runs at). Must return quickly (a permission prompt or the device may finish opening in the
    /// background) and never throw; null = no microphone.</summary>
    IMicrophoneStream? Open(int sampleRate);
}

/// <summary>A running host capture of 16-bit mono samples. Disposing it stops the capture.</summary>
public interface IMicrophoneStream : IDisposable
{
    /// <summary>The rate the captured samples are at (the core resamples them to the port's rate).</summary>
    int SampleRate { get; }

    /// <summary>A short description for logs and the capture report (device name and rate), never sample data.</summary>
    string Description { get; }

    /// <summary>Samples captured and not read yet. Any thread; never blocks.</summary>
    int Available { get; }

    /// <summary>Copies up to <c>dest.Length</c> of the oldest captured samples into <paramref name="dest"/> and returns how
    /// many; never blocks (0 when nothing is captured yet).</summary>
    int Read(Span<short> dest);
}

/// <summary>What the microphone setting chose: the host's default device, nothing (silence), a scripted tone or a named
/// device.</summary>
public enum MicrophoneSpecKind
{
    Default, None, Tone, Device
}

/// <summary>
/// The microphone setting ("Microfone", --microphone=): "" = the default capture device, "none" = no microphone (the
/// guest hears silence), "tone" or "tone:HZ" = a scripted sine tone (no device; checks the path without speaking),
/// anything else = a host device by name (exact or substring) or 0-based index.
/// </summary>
public readonly record struct MicrophoneSpec(MicrophoneSpecKind Kind, string Value, double ToneHz = MicrophoneSpec.DefaultToneHz)
{
    public const double DefaultToneHz = 440;

    public static readonly MicrophoneSpec Default = new(MicrophoneSpecKind.Default, "");
    public static readonly MicrophoneSpec None = new(MicrophoneSpecKind.None, "none");

    public static MicrophoneSpec Parse(string? value)
    {
        string text = value?.Trim().Trim('"') ?? "";
        if (text.Length == 0)
        {
            return Default;
        }

        if (text.Equals("none", StringComparison.OrdinalIgnoreCase))
        {
            return None;
        }

        if (text.Equals("tone", StringComparison.OrdinalIgnoreCase))
        {
            return new MicrophoneSpec(MicrophoneSpecKind.Tone, text);
        }

        if (text.StartsWith("tone:", StringComparison.OrdinalIgnoreCase)
            && double.TryParse(text.AsSpan(5), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double hz)
            && hz > 0)
        {
            return new MicrophoneSpec(MicrophoneSpecKind.Tone, text, hz);
        }

        return new MicrophoneSpec(MicrophoneSpecKind.Device, text);
    }

    /// <summary>Index into <paramref name="names"/> of the device this setting picks: the first one for the default, -1 for
    /// none, a tone or a name nothing matches.</summary>
    public int SelectDevice(IReadOnlyList<string> names)
    {
        if (Kind == MicrophoneSpecKind.Default)
        {
            return names.Count > 0 ? 0 : -1;
        }

        if (Kind != MicrophoneSpecKind.Device)
        {
            return -1;
        }

        if (int.TryParse(Value, out int index) && index >= 0 && index < names.Count)
        {
            return index;
        }

        for (int i = 0; i < names.Count; i++)
        {
            if (names[i].Equals(Value, StringComparison.OrdinalIgnoreCase))
            {
                return i;
            }
        }

        for (int i = 0; i < names.Count; i++)
        {
            if (names[i].Contains(Value, StringComparison.OrdinalIgnoreCase))
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>The source for this setting: null for "none", the scripted tone for "tone", else what
    /// <paramref name="device"/> builds for the default or a named device (the front-end's capture backend).</summary>
    public IMicrophoneSource? CreateSource(Func<MicrophoneSpec, IMicrophoneSource?> device)
    {
        return Kind switch
        {
            MicrophoneSpecKind.None => null,
            MicrophoneSpecKind.Tone => new ToneMicrophone(ToneHz),
            _ => device(this),
        };
    }
}

/// <summary>
/// A scripted microphone: a sine tone "captured" as time passes on a clock, like a device that delivers samples at its
/// rate. The default clock is the host's (a real device runs in real time); tests pass their own clock so the samples
/// available at each read are exactly what they decide.
/// </summary>
public sealed class ToneMicrophone : IMicrophoneSource
{
    private readonly double _frequency;
    private readonly short _amplitude;
    private readonly int? _rate;
    private readonly Func<double>? _seconds;

    /// <param name="frequency">Tone frequency in Hz.</param>
    /// <param name="amplitude">Peak sample value.</param>
    /// <param name="rate">The rate the stream captures at; null = the rate the port asks for.</param>
    /// <param name="seconds">The capture clock in seconds since the stream opened; null = the host's clock.</param>
    public ToneMicrophone(double frequency = MicrophoneSpec.DefaultToneHz, short amplitude = 8000, int? rate = null, Func<double>? seconds = null)
    {
        _frequency = frequency;
        _amplitude = amplitude;
        _rate = rate;
        _seconds = seconds;
    }

    public IReadOnlyList<string> DeviceNames => [$"tone {_frequency:0.##} Hz"];

    /// <summary>Streams opened so far (tests check the device is opened and closed with the port).</summary>
    public List<ToneStream> Streams { get; } = new();

    public IMicrophoneStream? Open(int sampleRate)
    {
        var stream = new ToneStream(this, _rate ?? sampleRate);
        lock (Streams)
        {
            Streams.Add(stream);
        }

        return stream;
    }

    public void Dispose()
    {
    }

    /// <summary>Sample <paramref name="index"/> of the tone at <paramref name="rate"/> Hz (what the stream delivers).</summary>
    public short Sample(long index, int rate) =>
        (short)Math.Round(_amplitude * Math.Sin(2 * Math.PI * _frequency * index / rate));

    public sealed class ToneStream : IMicrophoneStream
    {
        private readonly ToneMicrophone _owner;
        private readonly long _startTicks = Stopwatch.GetTimestamp();
        private readonly object _lock = new();
        private long _read;

        internal ToneStream(ToneMicrophone owner, int rate)
        {
            _owner = owner;
            SampleRate = rate;
        }

        public int SampleRate { get; }

        public string Description => $"tone {_owner._frequency:0.##} Hz at {SampleRate} Hz";

        public bool Disposed { get; private set; }

        /// <summary>Samples delivered so far.</summary>
        public long SamplesRead
        {
            get
            {
                lock (_lock)
                {
                    return _read;
                }
            }
        }

        private long Captured
        {
            get
            {
                double seconds = _owner._seconds?.Invoke() ?? Stopwatch.GetElapsedTime(_startTicks).TotalSeconds;
                return (long)Math.Floor(seconds * SampleRate + 1e-9);
            }
        }

        public int Available
        {
            get
            {
                lock (_lock)
                {
                    return Disposed ? 0 : (int)Math.Clamp(Captured - _read, 0, int.MaxValue);
                }
            }
        }

        public int Read(Span<short> dest)
        {
            lock (_lock)
            {
                if (Disposed)
                {
                    return 0;
                }

                int count = (int)Math.Clamp(Captured - _read, 0, dest.Length);
                for (int i = 0; i < count; i++)
                {
                    dest[i] = _owner.Sample(_read + i, SampleRate);
                }

                _read += count;
                return count;
            }
        }

        public void Dispose()
        {
            lock (_lock)
            {
                Disposed = true;
            }
        }
    }
}

/// <summary>
/// The samples a capture thread produced and the guest has not read yet, for hosts whose device only offers a blocking
/// read (Android's AudioRecord): the thread writes, the port's reads take. When the guest does not read (paused, a
/// loading screen), the oldest samples are overwritten: what is kept is the last <c>capacity</c> samples.
/// </summary>
public sealed class MicrophoneRing
{
    private readonly short[] _buffer;
    private readonly object _lock = new();
    private long _written, _read;

    public MicrophoneRing(int capacity)
    {
        _buffer = new short[Math.Max(1, capacity)];
    }

    public int Capacity => _buffer.Length;

    /// <summary>Samples overwritten before they were read.</summary>
    public long Overwritten { get; private set; }

    public int Available
    {
        get
        {
            lock (_lock)
            {
                return (int)(_written - _read);
            }
        }
    }

    public void Write(ReadOnlySpan<short> samples)
    {
        lock (_lock)
        {
            if (samples.Length > _buffer.Length)
            {
                // the older part would be overwritten at once: count it as written, keep the newest
                int skipped = samples.Length - _buffer.Length;
                _written += skipped;
                samples = samples[skipped..];
            }

            for (int i = 0; i < samples.Length; i++)
            {
                _buffer[(int)((_written + i) % _buffer.Length)] = samples[i];
            }

            _written += samples.Length;
            if (_written - _read > _buffer.Length)
            {
                Overwritten += _written - _read - _buffer.Length;
                _read = _written - _buffer.Length;
            }
        }
    }

    public int Read(Span<short> dest)
    {
        lock (_lock)
        {
            int count = (int)Math.Min(dest.Length, _written - _read);
            for (int i = 0; i < count; i++)
            {
                dest[i] = _buffer[(int)((_read + i) % _buffer.Length)];
            }

            _read += count;
            return count;
        }
    }
}
