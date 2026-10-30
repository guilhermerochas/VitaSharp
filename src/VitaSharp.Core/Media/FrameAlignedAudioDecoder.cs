namespace VitaSharp.Core.Media;

/// <summary>
/// An audio decoder that runs on its own schedule (Android's MediaCodec): frames go in, PCM comes out later and in
/// buffers of any size, with the decoder's own start-up delay (Android's MP3 decoder drops the first 529 samples, AAC
/// decoders may hold a frame back). <see cref="FrameAlignedAudioDecoder"/> turns it into SceAudiodec's frame in,
/// frame out. Platform neutral so the alignment is unit-tested on the desktop with a fake.
/// </summary>
public interface IAsyncAudioCodec : IDisposable
{
    string Name { get; }

    /// <summary>Output sample rate once the decoder reported it (0 before).</summary>
    int OutputSampleRate { get; }

    /// <summary>Queues one compressed frame; while waiting for an input buffer it may move decoded PCM into
    /// <paramref name="output"/>. False when the frame could not be queued (decoder error).</summary>
    bool Queue(ReadOnlySpan<byte> frame, PcmFifo output);

    /// <summary>Moves every decoded buffer that is ready into <paramref name="output"/>, waiting up to
    /// <paramref name="waitUs"/> microseconds for the first one when none is. Returns the samples per channel added.</summary>
    int Drain(PcmFifo output, int waitUs);

    /// <summary>Drops everything in flight (sceAudiodecClearContext).</summary>
    void Flush();
}

/// <summary>Interleaved S16 samples in a fixed channel layout, oldest first (grows as needed).</summary>
public sealed class PcmFifo(int channels)
{
    short[] _buf = new short[8192];
    int _start, _count; // in shorts

    public int Channels { get; } = Math.Max(1, channels);

    /// <summary>Samples per channel held.</summary>
    public int Frames => _count / Channels;

    /// <summary>Appends <paramref name="pcm"/> (interleaved, <paramref name="srcChannels"/> channels) converted to
    /// <see cref="Channels"/>: mono is duplicated, extra channels are dropped (FFmpeg's FrameDecoder does the same).</summary>
    public void Append(ReadOnlySpan<short> pcm, int srcChannels)
    {
        srcChannels = Math.Max(1, srcChannels);
        int frames = pcm.Length / srcChannels;
        Reserve(frames * Channels);
        int o = _start + _count;
        for (int i = 0; i < frames; i++)
            for (int c = 0; c < Channels; c++)
                _buf[o++] = pcm[i * srcChannels + Math.Min(c, srcChannels - 1)];
        _count += frames * Channels;
    }

    /// <summary>Moves up to <paramref name="frames"/> samples per channel into <paramref name="dst"/>; returns how many.</summary>
    public int Read(Span<short> dst, int frames)
    {
        int n = Math.Min(Math.Min(frames, Frames), dst.Length / Channels) * Channels;
        _buf.AsSpan(_start, n).CopyTo(dst);
        Skip(n / Channels);
        return n / Channels;
    }

    /// <summary>Drops the oldest <paramref name="frames"/> samples per channel.</summary>
    public void Skip(int frames)
    {
        int n = Math.Min(frames * Channels, _count);
        _start += n;
        _count -= n;
        if (_count == 0) _start = 0;
    }

    public void Clear() => _start = _count = 0;

    void Reserve(int more)
    {
        if (_start + _count + more <= _buf.Length) return;
        if (_count + more > _buf.Length) Array.Resize(ref _buf, Math.Max(_buf.Length * 2, _count + more));
        Array.Copy(_buf, _start, _buf, 0, _count);
        _start = 0;
    }
}

/// <summary>
/// SceAudiodec's frame in, frame out on top of an <see cref="IAsyncAudioCodec"/>: every <see cref="Decode"/> returns
/// exactly one frame of PCM (MP3: 1152 or 576 samples per channel from the frame header; AAC: 1024, 2048 with SBR), the
/// count FFmpeg's <see cref="FrameDecoder"/> returns, so the game's buffers, rates and clocks see the same thing on
/// every backend. Output that is not there yet (the decoder's start-up delay) is silence in front, so the stream comes
/// out a constant few hundred samples late instead of short; output that piles up beyond two frames (a decoder that
/// produced more than it was fed) is dropped, oldest first, to keep that delay bounded. Raw AAC frames are wrapped in an
/// ADTS header built from the AudioSpecificConfig, so the codec (configured for ADTS) sees each frame's exact end even
/// when the game hands over a whole ES slot with stale bytes after the frame.
/// </summary>
public sealed class FrameAlignedAudioDecoder : IAudioFrameDecoder
{
    /// <summary>Longest wait for the first output of the stream (decoder start-up), in microseconds.</summary>
    public const int FirstWaitUs = 20_000;
    /// <summary>Longest wait for a frame's output once the decoder produced something.</summary>
    public const int SteadyWaitUs = 5_000;

    readonly IAsyncAudioCodec _codec;
    readonly AudioFrameDecoderOptions _options;
    readonly PcmFifo _fifo;
    readonly (int ObjectType, int RateIndex, int Channels)? _adts;
    readonly int _coreRate;
    byte[] _input = new byte[2048];
    bool _primed;

    /// <summary>Frames that came out shorter than a frame (silence filled in) and samples dropped for piling up.</summary>
    public long PaddedFrames { get; private set; }
    public long DroppedSamples { get; private set; }

    public FrameAlignedAudioDecoder(IAsyncAudioCodec codec, AudioFrameDecoderOptions options)
    {
        _codec = codec;
        _options = options;
        _fifo = new PcmFifo(options.Channels);
        if (options.Codec == AudioFrameCodec.Aac && !options.Adts && options.AacConfig is { } asc)
        {
            _adts = AudioFrames.ParseAacConfig(asc);
            if (_adts is { } a) _coreRate = AudioFrames.AacRates[a.RateIndex];
        }
    }

    public string Name => _codec.Name;

    /// <summary>The codec cannot measure raw frames: the ADTS wrapper makes the whole buffer one frame.</summary>
    public int RawFrameLength(ReadOnlySpan<byte> data) => data.Length;

    /// <summary>Samples per channel of one frame, as FFmpeg returns them.</summary>
    public int FrameSamples(ReadOnlySpan<byte> frame)
    {
        if (_options.Codec == AudioFrameCodec.Mp3) return AudioFrames.Mp3FrameSamples(frame) is > 0 and var n ? n : 1152;
        int core = _options.Adts ? AudioFrames.AdtsSampleRate(frame) : _coreRate;
        // the decoder's output rate decides SBR (implicit SBR doubles it); before it reported one, the game's flag
        if (_codec.OutputSampleRate > 0 && core > 0) return _codec.OutputSampleRate >= core * 2 ? 2048 : 1024;
        return _options.Sbr ? 2048 : 1024;
    }

    public int Decode(ReadOnlySpan<byte> frame, Span<short> pcmOut, int outChannels)
    {
        if (frame.IsEmpty) return 0;
        ReadOnlySpan<byte> input = frame;
        if (_adts is { } a)
        {
            int len = Math.Min(frame.Length, 0x1FFF - 7);
            if (_input.Length < len + 7) _input = new byte[len + 7];
            AudioFrames.WriteAdtsHeader(_input, a.ObjectType, a.RateIndex, a.Channels, len);
            frame[..len].CopyTo(_input.AsSpan(7));
            input = _input.AsSpan(0, len + 7);
        }
        _codec.Queue(input, _fifo);
        int need = FrameSamples(frame);
        _codec.Drain(_fifo, 0);
        if (_fifo.Frames < need) _codec.Drain(_fifo, _primed ? SteadyWaitUs : FirstWaitUs);
        if (_fifo.Frames > 0) _primed = true;

        int ch = Math.Max(1, outChannels);
        need = Math.Min(need, pcmOut.Length / ch);
        int have = Math.Min(_fifo.Frames, need), pad = need - have;
        if (pad > 0)
        {
            PaddedFrames++;
            pcmOut[..(pad * ch)].Clear();
        }
        if (ch == _fifo.Channels) _fifo.Read(pcmOut[(pad * ch)..], have);
        else
        {
            var tmp = new short[have * _fifo.Channels];
            _fifo.Read(tmp, have);
            for (int i = 0; i < have; i++)
                for (int c = 0; c < ch; c++)
                    pcmOut[(pad + i) * ch + c] = tmp[i * _fifo.Channels + Math.Min(c, _fifo.Channels - 1)];
        }
        // more than two frames waiting: the decoder made more than it was given; keep the delay bounded
        int excess = _fifo.Frames - 2 * need;
        if (excess > 0)
        {
            _fifo.Skip(excess);
            DroppedSamples += excess;
        }
        return need;
    }

    public void Flush()
    {
        _codec.Flush();
        _fifo.Clear();
        _primed = false;
    }

    public void Dispose() => _codec.Dispose();
}
