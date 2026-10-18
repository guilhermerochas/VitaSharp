using FFmpeg.AutoGen;
using VitaSharp.Core.Util;

namespace VitaSharp.Core.Media;

/// <summary>
/// Locates the FFmpeg shared libraries (avcodec/avformat/avutil). They ship next to the executable in "ffmpeg/"
/// (desktop) or come from the platform package (Android). Without them, video playback and the MP3/AAC audio
/// decoders report "no media" instead of failing.
/// </summary>
public static class FFmpegLoader
{
    static readonly object Lock = new();
    static bool? _available;

    public static bool Available
    {
        get
        {
            lock (Lock)
            {
                return _available ??= Init();
            }
        }
    }

    static bool Init()
    {
        string[] candidates =
        [
            Environment.GetEnvironmentVariable("VITASHARP_FFMPEG") ?? "",
            Path.Combine(AppContext.BaseDirectory, "ffmpeg"),
            AppContext.BaseDirectory,
        ];
        foreach (var dir in candidates.Where(d => d.Length > 0 && Directory.Exists(d)))
        {
            if (!Directory.EnumerateFiles(dir, "avcodec*").Any()) continue;
            try
            {
                ffmpeg.RootPath = dir;
                uint v = ffmpeg.avcodec_version();
                ffmpeg.av_log_set_level(ffmpeg.AV_LOG_ERROR);
                Log.Info($"FFmpeg avcodec {v >> 16}.{(v >> 8) & 0xFF}.{v & 0xFF} loaded from {dir}");
                return true;
            }
            catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException or NotSupportedException)
            {
                Log.Warn($"FFmpeg in {dir} could not be loaded: {e.Message}");
            }
        }
        Log.Warn("FFmpeg libraries not found: videos (SceAvPlayer) and MP3/AAC decoding (SceAudiodec) are disabled");
        return false;
    }
}
