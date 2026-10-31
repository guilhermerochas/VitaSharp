using System.IO.Hashing;

namespace VitaSharp.Core.Media;

/// <summary>One compressed audio frame in, PCM out: SceAudiodec's MP3/AAC decoders (FFmpeg's <see cref="FrameDecoder"/>,
/// or a front-end's own, e.g. Android's MediaCodec).</summary>
public unsafe interface IAudioFrameDecoder : IDisposable
{
    /// <summary>Decodes exactly one frame of <paramref name="size"/> bytes; returns samples written per channel (0 on error).</summary>
    /// <param name="outChannels">channel layout of <paramref name="pcmOut"/> (0 = the stream's)</param>
    int Decode(byte* data, int size, Span<short> pcmOut, int outChannels = 0);

    /// <summary>Byte length of the raw (unframed) frame at <paramref name="data"/>; <paramref name="maxSize"/> when unknown.</summary>
    int RawFrameLength(byte* data, int maxSize);

    void Flush();
}

/// <summary>Creates SceAudiodec's AAC decoders: FFmpeg when it is loaded, else the front-end's <see cref="AacBackend"/>.</summary>
public static class AudioFrameDecoders
{
    /// <summary>(sample rate, channels, AudioSpecificConfig or null for ADTS) -> an AAC decoder; Android installs MediaCodec.</summary>
    public static Func<int, int, byte[]?, IAudioFrameDecoder?>? AacBackend { get; set; }

    public static IAudioFrameDecoder? CreateAac(int sampleRate, int channels, byte[]? audioSpecificConfig) =>
        (IAudioFrameDecoder?)FrameDecoder.Create(FFmpeg.AutoGen.AVCodecID.AV_CODEC_ID_AAC, sampleRate, channels, audioSpecificConfig)
        ?? AacBackend?.Invoke(sampleRate, channels, audioSpecificConfig);
}

/// <summary>
/// Lengths of the raw AAC frames SceMp4 handed out, so a decoder that cannot measure a raw frame (MediaCodec has no
/// "bytes consumed") still decodes exactly one: games copy each unit into a slot of their own (F1 2011: 256/512-byte
/// slots) and pass SceAudiodec the slot with a fixed maximum size, and the bytes after the frame are stale data. Keyed by
/// the frame's first 8 bytes, verified with a hash of the whole frame; bounded.
/// </summary>
public static class RawAudioFrameLengths
{
    const int MaxEntries = 4096;
    static readonly Dictionary<ulong, List<(int Length, ulong Hash)>> _byPrefix = new();
    static readonly Queue<ulong> _order = new();

    static ulong Prefix(ReadOnlySpan<byte> d) => d.Length >= 8 ? BitConverter.ToUInt64(d) : XxHash64.HashToUInt64(d) | 1UL << 63;

    public static void Record(ReadOnlySpan<byte> frame)
    {
        if (frame.Length == 0) return;
        ulong key = Prefix(frame[..Math.Min(8, frame.Length)]), hash = XxHash64.HashToUInt64(frame);
        lock (_byPrefix)
        {
            if (!_byPrefix.TryGetValue(key, out var list)) _byPrefix[key] = list = [];
            if (list.Contains((frame.Length, hash))) return;
            list.Add((frame.Length, hash));
            _order.Enqueue(key);
            while (_order.Count > MaxEntries && _order.Dequeue() is var old && _byPrefix.TryGetValue(old, out var l))
            {
                l.RemoveAt(0);
                if (l.Count == 0) _byPrefix.Remove(old);
            }
        }
    }

    /// <summary>The length of a recorded frame at the start of <paramref name="data"/>, or 0.</summary>
    public static int Find(ReadOnlySpan<byte> data)
    {
        lock (_byPrefix)
        {
            // frames shorter than 8 bytes are keyed by their whole content, longer ones by their first 8 bytes; the
            // longest recorded frame that matches wins (a short frame may equal the start of a longer one)
            int best = 0;
            for (int n = Math.Min(8, data.Length); n >= 1 && best == 0; n--)
            {
                if (!_byPrefix.TryGetValue(Prefix(data[..n]), out var list)) continue;
                foreach (var (len, hash) in list)
                    if (len > best && len <= data.Length && XxHash64.HashToUInt64(data[..len]) == hash) best = len;
            }
            return best;
        }
    }

    public static void Clear() { lock (_byPrefix) { _byPrefix.Clear(); _order.Clear(); } }
}
