namespace VitaSharp.Core.Media;

/// <summary>A decoded picture: YUV 4:2:0 planes (Y full size, U and V at half resolution), owned by the decoder.</summary>
public sealed class DecodedPicture
{
    public int Width, Height;
    public byte[] Y = [], U = [], V = [];
    public long Pts;
    public bool FullRange;
    public bool Bt709;
}

/// <summary>
/// An H.264 decoder fed with Annex B access units (start-code delimited NAL units, SPS/PPS in band), the way the
/// Vita's SceAvcdec receives them. Pictures come out in presentation order through <see cref="Take"/>.
/// Implementations: FFmpeg (software or FFmpeg hwaccel: D3D11VA, VAAPI, VideoToolbox) on desktop; Android's
/// MediaCodec later (src/VitaSharp.Android). Not thread-safe: one caller at a time.
/// </summary>
public interface IVideoDecoder : IDisposable
{
    /// <summary>Backend and mode, e.g. "ffmpeg-sw", "ffmpeg-d3d11va" (logs, PERF reports).</summary>
    string Name { get; }

    /// <summary>True while a hardware decoder does the work (false after a fallback to software).</summary>
    bool IsHardware { get; }

    /// <summary>Feeds one access unit; decoded pictures become available through <see cref="Take"/>.</summary>
    void Send(ReadOnlySpan<byte> accessUnit, long pts);

    /// <summary>End of stream: outputs the pictures still held for reordering, then accepts a new stream.</summary>
    void Flush();

    /// <summary>Discards everything in flight (seek, sceAvcdecDecodeFlush).</summary>
    void Reset();

    /// <summary>Next decoded picture, or null.</summary>
    DecodedPicture? Take();

    /// <summary>Hands a picture from <see cref="Take"/> back once its planes were consumed (buffer reuse).</summary>
    void Recycle(DecodedPicture picture);
}

/// <summary>Creates video decoders. Front-ends may install another backend in <see cref="VideoDecoders.Backend"/>.</summary>
public interface IVideoDecoderBackend
{
    string Name { get; }

    /// <summary>A new H.264 decoder, or null when the backend cannot decode H.264 on this host.</summary>
    IVideoDecoder? CreateH264(VideoDecoderOptions options);
}

/// <summary>Hardware decoding policy: try the platform's hardware decoder first (falling back to software), or never.</summary>
public enum HardwareDecode
{
    Auto,
    Off,
}

/// <param name="MaxWidth">Largest picture the stream may carry (0 = unknown), a hint for hardware decoder setup.</param>
/// <param name="MaxHeight">Largest picture height (0 = unknown).</param>
public sealed record VideoDecoderOptions(int MaxWidth = 0, int MaxHeight = 0);

/// <summary>The video decoder backend used by SceAvcdec/SceVideodec and the SceAvPlayer media player.</summary>
public static class VideoDecoders
{
    /// <summary>Default: FFmpeg (hardware through hwaccel when available, software otherwise).</summary>
    public static IVideoDecoderBackend Backend { get; set; } = new FFmpegVideoBackend();

    public static IVideoDecoder? CreateH264(VideoDecoderOptions? options = null) => Backend.CreateH264(options ?? new VideoDecoderOptions());
}
