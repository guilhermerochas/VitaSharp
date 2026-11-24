namespace VitaSharp.Core.Host;

/// <summary>
/// Turns a host microphone stream into the grains of a SceAudioIn port: each <see cref="Fill"/> writes one grain at the
/// port's rate, resampled (linear) from the host stream's rate when they differ. The pace is the guest's: the port's
/// blocking read waits on the guest clock and asks for one grain per period, whatever the host device does; this class
/// only bridges the two clocks with a small queue:
/// <list type="bullet">
/// <item>at the start, and when the host fell behind (it delivered less than a grain), the grain is silent and the
/// samples wait for the next one (the queue fills again);</item>
/// <item>when the host got ahead (guest paused or slow, or a device clock slightly faster), the oldest samples are
/// dropped down to one grain, so what the guest hears stays at most a few grains behind the speaker.</item>
/// </list>
/// Muted, or without a stream, the grain is silent; muted, the captured samples are still consumed, so unmuting gives
/// what is said from then on. No allocation per grain (the buffers are sized when the port opens).
/// </summary>
public sealed class MicrophoneFeed : IDisposable
{
    /// <summary>The queue in front of the resampler never holds more than this many grains of host samples.</summary>
    public const int MaxQueuedGrains = 4;

    private readonly IMicrophoneStream? _stream;
    private readonly int _grain;
    /// <summary>Host samples per port sample.</summary>
    private readonly double _ratio;
    private readonly short[] _queue;
    private readonly short[] _discard = new short[1024];
    private int _queued;
    /// <summary>Fractional position of the next output sample in <see cref="_queue"/>.</summary>
    private double _position;

    public MicrophoneFeed(IMicrophoneStream? stream, int portRate, int grain)
    {
        _stream = stream;
        _grain = grain;
        PortRate = portRate;
        int hostRate = stream?.SampleRate is > 0 and var rate ? rate : portRate;
        _ratio = (double)hostRate / portRate;
        int hostGrain = (int)Math.Ceiling(grain * _ratio) + 2;
        _queue = new short[hostGrain * MaxQueuedGrains];
    }

    public int PortRate { get; }

    public int Grain => _grain;

    /// <summary>The host stream, null when the port has no microphone (silence).</summary>
    public IMicrophoneStream? Stream => _stream;

    /// <summary>Grains filled, grains that were silent because the host had not captured enough (start-up, host behind),
    /// grains filled while muted, and host samples dropped because the host got ahead.</summary>
    public long Grains { get; private set; }

    public long ShortGrains { get; private set; }

    public long MutedGrains { get; private set; }

    public long DroppedSamples { get; private set; }

    /// <summary>Writes one grain into <paramref name="grain"/> (its length is the port's grain).</summary>
    public void Fill(Span<short> grain, bool muted)
    {
        Grains++;
        if (_stream == null)
        {
            grain.Clear();
            return;
        }

        int need = Needed();
        int available = _stream.Available;
        int room = _queue.Length - _queued;
        if (available > room)
        {
            // the host got ahead: keep the newest samples, one grain's worth
            int keep = Math.Min(need, _queue.Length);
            int total = _queued + available;
            int drop = total - keep;
            int fromQueue = Math.Min(drop, _queued);
            Array.Copy(_queue, fromQueue, _queue, 0, _queued - fromQueue);
            _queued -= fromQueue;
            Skip(drop - fromQueue);
            DroppedSamples += drop;
            _position = 0;
            need = Needed();
            available = _stream.Available;
        }

        if (_queued < need)
        {
            _queued += _stream.Read(_queue.AsSpan(_queued, Math.Min(_queue.Length - _queued, Math.Max(0, available))));
        }

        if (muted)
        {
            MutedGrains++;
        }

        if (_queued < need)
        {
            // not enough captured yet: a silent grain, the samples stay for the next one
            ShortGrains++;
            grain.Clear();
            return;
        }

        if (muted)
        {
            grain.Clear();
        }
        else
        {
            Resample(grain);
        }

        double end = _position + _grain * _ratio;
        int consumed = Math.Min((int)end, _queued);
        Array.Copy(_queue, consumed, _queue, 0, _queued - consumed);
        _queued -= consumed;
        _position = end - consumed;
    }

    /// <summary>Host samples the next grain reads: up to the last output sample's position, plus the one after it when
    /// that position falls between two samples.</summary>
    private int Needed()
    {
        double last = _position + (_grain - 1) * _ratio;
        int index = (int)last;
        return last > index ? index + 2 : index + 1;
    }

    private void Resample(Span<short> grain)
    {
        if (_ratio == 1 && _position == 0)
        {
            _queue.AsSpan(0, grain.Length).CopyTo(grain);
            return;
        }

        for (int i = 0; i < grain.Length; i++)
        {
            double x = _position + i * _ratio;
            int index = (int)x;
            double fraction = x - index;
            int sample = _queue[index];
            if (fraction > 0)
            {
                sample = (int)Math.Round(sample + (_queue[index + 1] - sample) * fraction);
            }

            grain[i] = (short)sample;
        }
    }

    private void Skip(int count)
    {
        while (count > 0)
        {
            int read = _stream!.Read(_discard.AsSpan(0, Math.Min(count, _discard.Length)));
            if (read <= 0)
            {
                return;
            }

            count -= read;
        }
    }

    /// <summary>One line for the logs and the capture report: the port's format and pace and the host source, never the
    /// samples.</summary>
    public string Describe() =>
        $"grain {_grain} at {PortRate} Hz, source {(_stream == null ? "none (silence)" : _stream.Description)}; "
        + $"grains {Grains}, silent while the host filled {ShortGrains}, muted {MutedGrains}, host samples dropped {DroppedSamples}";

    public void Dispose()
    {
        _stream?.Dispose();
    }
}
