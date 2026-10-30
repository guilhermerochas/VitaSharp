namespace VitaSharp.Core.Media;

/// <summary>
/// An MP4 demuxer for SceMp4: stream descriptions and access units in file order, H.264 as Annex B with SPS/PPS in
/// front of every key frame, AAC as raw frames, timestamps in 90 kHz ticks. Implementations: FFmpeg on the desktop
/// (<see cref="Mp4Demuxer"/>); Android's MediaExtractor (src/VitaSharp.Android/Media).
/// </summary>
public interface IMp4Demuxer : IDisposable
{
    IReadOnlyList<DemuxedStream> Streams { get; }
    long Duration90k { get; }

    /// <summary>Next unit of an enabled stream, or null at the end.</summary>
    DemuxedUnit? Next(Func<int, bool> enabled);

    /// <summary>Moves to the key frame at or before <paramref name="pts90k"/>.</summary>
    void Seek(long pts90k);
}

/// <summary>The MP4 demuxer backend used by SceMp4.</summary>
public static class Mp4Demuxers
{
    /// <summary>Opens a file, or null when this backend cannot read it (then FFmpeg is tried, when it is available).
    /// Android installs MediaExtractor here.</summary>
    public static Func<string, IMp4Demuxer?>? Backend { get; set; }

    public static IMp4Demuxer? Open(string path) => Backend?.Invoke(path) ?? Mp4Demuxer.Open(path);

    /// <summary>Microseconds (MediaExtractor's sample times) to the 90 kHz ticks SceMp4 reports.</summary>
    public static long UsTo90k(long us) => us * 9 / 100;

    /// <summary>90 kHz ticks to microseconds.</summary>
    public static long Ticks90kToUs(long ticks) => ticks * 100 / 9;
}
