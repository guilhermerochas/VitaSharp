using FFmpeg.AutoGen;

namespace VitaSharp.Core.Media;

public enum AudioFrameCodec { Mp3, Aac }

/// <summary>What a frame decoder is created for (sceAudiodecCreateDecoder's init parameters).</summary>
/// <param name="SampleRate">AAC: the output rate the game declared (with SBR twice the core rate); MP3: 0 (from the stream).</param>
/// <param name="Channels">Channels of the PCM the game takes (1 or 2).</param>
/// <param name="AacConfig">Raw (headerless, MP4-style) AAC: the AudioSpecificConfig; null for ADTS and MP3.</param>
/// <param name="Adts">AAC frames carry ADTS headers.</param>
/// <param name="Sbr">AAC: the game declared SBR (HE-AAC), 2048 samples per frame.</param>
public sealed record AudioFrameDecoderOptions(AudioFrameCodec Codec, int SampleRate, int Channels, byte[]? AacConfig = null,
    bool Adts = false, bool Sbr = false);

/// <summary>
/// A frame-in, frame-out audio decoder, the way the Vita's SceAudiodec works: every compressed MP3 or AAC frame becomes
/// one full frame of interleaved S16 PCM (1152 or 576 samples per channel for MP3, 1024 for AAC, 2048 with SBR), the
/// first one included (no encoder-delay trimming). Implementations: FFmpeg on the desktop (<see cref="FrameDecoder"/>);
/// Android's MediaCodec through <see cref="FrameAlignedAudioDecoder"/>. Not thread-safe: one caller at a time.
/// </summary>
public interface IAudioFrameDecoder : IDisposable
{
    /// <summary>Backend, e.g. "ffmpeg", "mediacodec c2.android.aac.decoder" (logs).</summary>
    string Name { get; }

    /// <summary>Raw AAC has no frame header: the byte length of the frame at the start of <paramref name="data"/>
    /// (what the hardware reports as inputEsSize), or <c>data.Length</c> when the backend cannot tell.</summary>
    int RawFrameLength(ReadOnlySpan<byte> data);

    /// <summary>Decodes exactly one frame; returns the samples per channel written (0 on error).</summary>
    /// <param name="outChannels">Channel layout of <paramref name="pcmOut"/>: mono is duplicated, extra channels dropped.</param>
    int Decode(ReadOnlySpan<byte> frame, Span<short> pcmOut, int outChannels);

    /// <summary>Forgets the stream state (sceAudiodecClearContext).</summary>
    void Flush();
}

/// <summary>Creates frame decoders. Front-ends may install another backend in <see cref="AudioFrameDecoders.Backend"/>.</summary>
public interface IAudioFrameDecoderBackend
{
    string Name { get; }

    /// <summary>A new decoder, or null when this host cannot decode the codec.</summary>
    IAudioFrameDecoder? Create(AudioFrameDecoderOptions options);
}

/// <summary>FFmpeg's MP3 and AAC decoders (<see cref="FrameDecoder"/>), the desktop's backend.</summary>
public sealed class FFmpegAudioFrameBackend : IAudioFrameDecoderBackend
{
    public string Name => "ffmpeg";

    public IAudioFrameDecoder? Create(AudioFrameDecoderOptions o) => o.Codec switch
    {
        AudioFrameCodec.Mp3 => FrameDecoder.Create(AVCodecID.AV_CODEC_ID_MP3, o.SampleRate, o.Channels),
        _ => FrameDecoder.Create(AVCodecID.AV_CODEC_ID_AAC, o.SampleRate, o.Channels, o.Adts ? null : o.AacConfig),
    };
}

/// <summary>The frame decoder backend used by SceAudiodec (MP3, AAC).</summary>
public static class AudioFrameDecoders
{
    /// <summary>Default: FFmpeg. Android installs its MediaCodec backend.</summary>
    public static IAudioFrameDecoderBackend Backend { get; set; } = new FFmpegAudioFrameBackend();

    public static IAudioFrameDecoder? Create(AudioFrameDecoderOptions options) => Backend.Create(options);
}

/// <summary>Frame headers of MP3 and AAC streams: lengths, samples per frame, AudioSpecificConfig and ADTS headers.</summary>
public static class AudioFrames
{
    static readonly int[] Mp3RateV1 = [44100, 48000, 32000, 0];
    /// <summary>MPEG-4 sampling frequency indices (AudioSpecificConfig, ADTS).</summary>
    public static readonly int[] AacRates = [96000, 88200, 64000, 48000, 44100, 32000, 24000, 22050, 16000, 12000, 11025, 8000, 7350];

    /// <summary>Length in bytes of the MPEG audio layer III frame starting at <paramref name="p"/>, or 0 (pure
    /// header parsing, no FFmpeg call).</summary>
    public static int Mp3FrameLength(ReadOnlySpan<byte> p) => FrameDecoder.Mp3FrameLength(p);

    /// <summary>Samples per channel of the layer III frame at <paramref name="p"/>: 1152 (MPEG-1), 576 (MPEG-2/2.5),
    /// 0 when it is not one.</summary>
    public static int Mp3FrameSamples(ReadOnlySpan<byte> p) => Mp3Header(p, out int version, out _) ? version == 3 ? 1152 : 576 : 0;

    /// <summary>Sample rate of the layer III frame at <paramref name="p"/>, or 0.</summary>
    public static int Mp3SampleRate(ReadOnlySpan<byte> p) => Mp3Header(p, out _, out int rate) ? rate : 0;

    static bool Mp3Header(ReadOnlySpan<byte> p, out int version, out int rate)
    {
        version = rate = 0;
        if (p.Length < 4 || p[0] != 0xFF || (p[1] & 0xE0) != 0xE0) return false;
        version = (p[1] >> 3) & 3; // 3 = MPEG1, 2 = MPEG2, 0 = MPEG2.5
        int layer = (p[1] >> 1) & 3;   // 1 = layer III
        int srIdx = (p[2] >> 2) & 3;
        if (version == 1 || layer != 1 || srIdx == 3) return false;
        rate = Mp3RateV1[srIdx] >> (version == 3 ? 0 : version == 2 ? 1 : 2);
        return true;
    }

    /// <summary>Length of the ADTS frame (header included) at <paramref name="p"/>, or 0 when it is not ADTS.</summary>
    public static int AdtsFrameLength(ReadOnlySpan<byte> p) => FrameDecoder.AdtsFrameLength(p);

    /// <summary>Sample rate of the AAC core (without SBR) of the ADTS frame at <paramref name="p"/>, or 0.</summary>
    public static int AdtsSampleRate(ReadOnlySpan<byte> p)
    {
        if (p.Length < 7 || p[0] != 0xFF || (p[1] & 0xF6) != 0xF0) return 0;
        int idx = (p[2] >> 2) & 0xF;
        return idx < AacRates.Length ? AacRates[idx] : 0;
    }

    /// <summary>AudioSpecificConfig for AAC-LC with the given rate and channel count.</summary>
    public static byte[] AacConfig(int sampleRate, int channels) => FrameDecoder.AacConfig(sampleRate, channels);

    /// <summary>Object type, sampling frequency index and channel configuration of a (two-byte form)
    /// AudioSpecificConfig; null when it is too short or uses the escape values.</summary>
    public static (int ObjectType, int RateIndex, int Channels)? ParseAacConfig(ReadOnlySpan<byte> asc)
    {
        if (asc.Length < 2) return null;
        int objectType = asc[0] >> 3, rateIndex = ((asc[0] & 7) << 1) | (asc[1] >> 7), channels = (asc[1] >> 3) & 0xF;
        if (objectType is 0 or 31 || rateIndex >= AacRates.Length) return null;
        return (objectType, rateIndex, channels);
    }

    /// <summary>
    /// A 7-byte ADTS header (MPEG-4, no CRC) for a raw AAC frame of <paramref name="payloadLength"/> bytes: decoders that
    /// take ADTS frame by frame (Android's MediaCodec) then see exactly where the frame ends. Profile = object type - 1
    /// (ADTS carries the four MPEG-2 profiles: Main, LC, SSR, LTP; HE-AAC streams signal their LC core, as their ADTS
    /// form does).
    /// </summary>
    public static void WriteAdtsHeader(Span<byte> dst, int objectType, int rateIndex, int channels, int payloadLength)
    {
        int profile = Math.Clamp(objectType is 5 or 29 ? 1 : objectType - 1, 0, 3);
        int len = Math.Min(payloadLength + 7, 0x1FFF);
        dst[0] = 0xFF;
        dst[1] = 0xF1;                                   // sync, MPEG-4, layer 0, no CRC
        dst[2] = (byte)((profile << 6) | (rateIndex << 2) | ((channels >> 2) & 1));
        dst[3] = (byte)(((channels & 3) << 6) | (len >> 11));
        dst[4] = (byte)(len >> 3);
        dst[5] = (byte)(((len & 7) << 5) | 0x1F);       // buffer fullness 0x7FF (VBR)
        dst[6] = 0xFC;                                   // one raw data block
    }
}
