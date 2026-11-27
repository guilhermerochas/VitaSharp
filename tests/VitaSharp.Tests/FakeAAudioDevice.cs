using System.Buffers.Binary;
using VitaSharp.Core.Host;
using VitaSharp.Mobile.Core.Audio;

namespace VitaSharp.Tests;

/// <summary>
/// The phone's AAudio output, without the phone: an <see cref="IAudioBackend"/> that builds every guest port's stream
/// exactly as <c>AndroidAudioBackend.OpenStream</c> does (a <see cref="BufferedPcmStream"/> over an
/// <see cref="IPcmSink"/>), over fake AAudio streams that behave as the user's phone logged them: AAudio normal, shared,
/// 48000 Hz, a buffer capacity of 4104 frames and a 2052-frame burst (42.8 ms). The device's time is the test's: it
/// moves only when <see cref="Run"/> is called (the guest is late) or when a blocking write finds the buffer full (the
/// guest waits for the device, as <c>AAudioStream_write</c> does). At each burst the device pulls one burst from every
/// started stream, a stream that holds less plays it plus silence and counts an xrun (getXRunCount), and the shared
/// mixer adds the streams (as the system mixer does for shared streams) into one stereo recording, kept as a WAV in
/// memory (<see cref="ToWav"/>). Nothing here reads the host's clock.
/// </summary>
public sealed class FakeAAudioDevice : IAudioBackend
{
    public const int DeviceRate = 48000;
    public const int CapacityFrames = 4104;
    public const int BurstFrames = 2052;

    private readonly object _lock = new();
    private readonly List<FakeAAudioStream> _streams = [];
    private readonly List<BufferedPcmStream> _portStreams = [];
    private readonly List<short> _mix = [];
    private long _sinceBurst;

    /// <summary>Every stream the device opened, in order (closed ones too).</summary>
    public readonly List<FakeAAudioStream> Opened = [];

    /// <summary>Bursts the device played.</summary>
    public int Bursts { get; private set; }

    public IAudioStream OpenStream(int sampleRate, int channels, int grainSamples)
    {
        // AndroidAudioBackend.OpenStream: the stream's buffering policy and resampling are BufferedPcmStream's
        BufferedPcmStream stream = new(OpenSink, sampleRate, channels, grainSamples, Closed, stopRequested: () => false);
        lock (_lock)
        {
            _portStreams.Add(stream);
        }

        return stream;
    }

    private IPcmSink OpenSink(int channels)
    {
        FakeAAudioStream sink = new(this, channels);
        lock (_lock)
        {
            _streams.Add(sink);
            Opened.Add(sink);
        }

        return sink;
    }

    private void Closed(BufferedPcmStream stream)
    {
        lock (_lock)
        {
            _portStreams.Remove(stream);
        }
    }

    internal void Remove(FakeAAudioStream sink)
    {
        lock (_lock)
        {
            _streams.Remove(sink);
        }
    }

    /// <summary>AndroidAudioBackend.Describe: the device, then each stream's buffering (AUDIO summary, capture).</summary>
    public string Describe()
    {
        BufferedPcmStream[] all;
        lock (_lock)
        {
            all = [.. _portStreams];
        }

        string head = $"fake AAudio (phone profile), device {DeviceRate} Hz, {BurstFrames} frames per buffer";
        return all.Length == 0 ? head : head + "; " + string.Join("; ", all.Select(s => s.Describe()));
    }

    /// <summary>The device plays <paramref name="frames"/> frames of time without the guest writing (whole bursts: the
    /// device pulls a burst each <see cref="BurstFrames"/> frames).</summary>
    public void Run(int frames)
    {
        lock (_lock)
        {
            _sinceBurst += frames;
            while (_sinceBurst >= BurstFrames)
            {
                _sinceBurst -= BurstFrames;
                BurstLocked();
            }
        }
    }

    /// <summary>A blocking write found the buffer full: time passes up to the device's next burst.</summary>
    internal void WaitForBurst()
    {
        lock (_lock)
        {
            _sinceBurst = 0;
            BurstLocked();
        }
    }

    private void BurstLocked()
    {
        Bursts++;
        int[] mixed = new int[BurstFrames * 2];
        foreach (FakeAAudioStream stream in _streams)
        {
            stream.PullLocked(mixed);
        }

        foreach (int sample in mixed)
        {
            _mix.Add((short)Math.Clamp(sample, short.MinValue, short.MaxValue));
        }
    }

    /// <summary>What the device played (stereo, interleaved), as a 16-bit PCM WAV file.</summary>
    public byte[] ToWav()
    {
        short[] samples;
        lock (_lock)
        {
            samples = [.. _mix];
        }

        byte[] wav = new byte[44 + samples.Length * 2];
        Span<byte> header = wav.AsSpan(0, 44);
        "RIFF"u8.CopyTo(header);
        BinaryPrimitives.WriteInt32LittleEndian(header[4..], wav.Length - 8);
        "WAVEfmt "u8.CopyTo(header[8..]);
        BinaryPrimitives.WriteInt32LittleEndian(header[16..], 16);
        BinaryPrimitives.WriteInt16LittleEndian(header[20..], 1);
        BinaryPrimitives.WriteInt16LittleEndian(header[22..], 2);
        BinaryPrimitives.WriteInt32LittleEndian(header[24..], DeviceRate);
        BinaryPrimitives.WriteInt32LittleEndian(header[28..], DeviceRate * 4);
        BinaryPrimitives.WriteInt16LittleEndian(header[32..], 4);
        BinaryPrimitives.WriteInt16LittleEndian(header[34..], 16);
        "data"u8.CopyTo(header[36..]);
        BinaryPrimitives.WriteInt32LittleEndian(header[40..], samples.Length * 2);
        for (int i = 0; i < samples.Length; i++)
        {
            BinaryPrimitives.WriteInt16LittleEndian(wav.AsSpan(44 + i * 2), samples[i]);
        }

        return wav;
    }

    /// <summary>The interleaved samples of a WAV written by <see cref="ToWav"/> (its header checked).</summary>
    public static short[] ReadWav(byte[] wav, out int rate, out int channels)
    {
        Assert.True(wav.AsSpan(0, 4).SequenceEqual("RIFF"u8) && wav.AsSpan(8, 8).SequenceEqual("WAVEfmt "u8), "not a WAV file");
        Assert.Equal(1, BinaryPrimitives.ReadInt16LittleEndian(wav.AsSpan(20)));
        channels = BinaryPrimitives.ReadInt16LittleEndian(wav.AsSpan(22));
        rate = BinaryPrimitives.ReadInt32LittleEndian(wav.AsSpan(24));
        Assert.Equal(16, BinaryPrimitives.ReadInt16LittleEndian(wav.AsSpan(34)));
        int bytes = BinaryPrimitives.ReadInt32LittleEndian(wav.AsSpan(40));
        Assert.Equal(wav.Length - 44, bytes);
        short[] samples = new short[bytes / 2];
        for (int i = 0; i < samples.Length; i++)
        {
            samples[i] = BinaryPrimitives.ReadInt16LittleEndian(wav.AsSpan(44 + i * 2));
        }

        return samples;
    }

    public void SetPaused(bool paused)
    {
        BufferedPcmStream[] all;
        lock (_lock)
        {
            all = [.. _portStreams];
        }

        foreach (BufferedPcmStream stream in all)
        {
            if (paused)
            {
                stream.Pause();
            }
            else
            {
                stream.Resume();
            }
        }
    }

    public void Dispose()
    {
        BufferedPcmStream[] all;
        lock (_lock)
        {
            all = [.. _portStreams];
        }

        foreach (BufferedPcmStream stream in all)
        {
            stream.Dispose();
        }
    }
}

/// <summary>
/// One AAudio output stream of <see cref="FakeAAudioDevice"/> (AAudioSink's contract): a ring of at most
/// <see cref="FakeAAudioDevice.CapacityFrames"/> frames, of which <see cref="SetBufferSize"/> may be used (clamped to the
/// capacity, as AAudioStream_setBufferSizeInFrames does); a write with a timeout on a full buffer waits for the device's
/// next burst; frames read, frames written and xruns as AAudio counts them. Everything written is kept
/// (<see cref="Written"/>), as the stream received it.
/// </summary>
public sealed class FakeAAudioStream : IPcmSink
{
    private readonly FakeAAudioDevice _device;
    private readonly Queue<short> _ring = new();
    private int _bufferFrames = FakeAAudioDevice.CapacityFrames;
    private long _written;
    private long _read;
    private int _xruns;
    private bool _started;
    private bool _wasShort;

    /// <summary>Every sample written, in order (interleaved).</summary>
    public readonly List<short> Written = [];

    /// <summary>Dry spells: runs of bursts in which the stream had less than a burst queued.</summary>
    public int DrySpells { get; private set; }

    public bool Disposed { get; private set; }

    internal FakeAAudioStream(FakeAAudioDevice device, int channels)
    {
        _device = device;
        Channels = channels;
    }

    public int SampleRate => FakeAAudioDevice.DeviceRate;
    public int Channels { get; }
    public int FramesPerBurst => FakeAAudioDevice.BurstFrames;
    // AAudioSink.Description, for the profile the phone logged
    public string Description => $"AAudio normal shared, capacity {FakeAAudioDevice.CapacityFrames} frames";
    public bool Disconnected => false;
    public long FramesWritten => Volatile.Read(ref _written);
    public long FramesRead => Volatile.Read(ref _read);
    public int XRunCount => Volatile.Read(ref _xruns);

    /// <summary>The frames the stream may hold now (the buffer size the stream took).</summary>
    public int BufferFrames => _bufferFrames;

    public int SetBufferSize(int frames)
    {
        _bufferFrames = Math.Clamp(frames, 1, FakeAAudioDevice.CapacityFrames);
        return _bufferFrames;
    }

    public int Write(ReadOnlySpan<short> interleaved, long timeoutNanos)
    {
        int frames = interleaved.Length / Channels;
        int room = Room();
        if (room == 0 && timeoutNanos > 0 && _started)
        {
            // AAudioStream_write blocks until the device has read: the guest waits for the next burst
            _device.WaitForBurst();
            room = Room();
        }

        int count = Math.Min(room, frames);
        lock (_ring)
        {
            for (int i = 0; i < count * Channels; i++)
            {
                _ring.Enqueue(interleaved[i]);
                Written.Add(interleaved[i]);
            }

            _written += count;
        }

        return count;
    }

    private int Room()
    {
        lock (_ring)
        {
            return (int)Math.Max(0, _bufferFrames - (_written - _read));
        }
    }

    /// <summary>The device's burst: one burst of this stream added into <paramref name="mixed"/> (stereo), silence for
    /// what is missing (one xrun per short burst); a stream not started plays nothing.</summary>
    internal void PullLocked(int[] mixed)
    {
        if (!_started)
        {
            return;
        }

        lock (_ring)
        {
            int available = _ring.Count / Channels;
            int frames = Math.Min(available, FakeAAudioDevice.BurstFrames);
            for (int frame = 0; frame < frames; frame++)
            {
                short left = _ring.Dequeue();
                short right = Channels > 1 ? _ring.Dequeue() : left;
                for (int extra = 2; extra < Channels; extra++)
                {
                    _ring.Dequeue();
                }

                mixed[2 * frame] += left;
                mixed[2 * frame + 1] += right;
            }

            _read += frames;
            bool isShort = frames < FakeAAudioDevice.BurstFrames;
            if (isShort)
            {
                _xruns++;
                if (!_wasShort)
                {
                    DrySpells++;
                }
            }

            _wasShort = isShort;
        }
    }

    public void Start() => _started = true;

    public void Pause() => _started = false;

    public void Dispose()
    {
        Disposed = true;
        _started = false;
        _device.Remove(this);
    }
}
