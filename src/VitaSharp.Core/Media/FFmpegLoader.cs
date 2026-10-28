using FFmpeg.AutoGen;
using VitaSharp.Core.Util;

namespace VitaSharp.Core.Media;

/// <summary>
/// Locates the FFmpeg shared libraries (avcodec/avformat/avutil). They ship next to the executable in "ffmpeg/"
/// (Windows and Linux, downloaded at build time by build/FFmpeg.targets) or come from the system: Homebrew on macOS,
/// the distribution's packages on Linux. Without them, video playback and the MP3/AAC audio decoders report
/// "no media" instead of failing.
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
            Environment.GetEnvironmentVariable(EnvVars.Ffmpeg) ?? "",
            Path.Combine(AppContext.BaseDirectory, "ffmpeg"),
            AppContext.BaseDirectory,
            .. OperatingSystem.IsMacOS() ? new[] { "/opt/homebrew/lib", "/usr/local/lib" } : [],
        ];
        // "avcodec-62.dll" on Windows, "libavcodec.so.62" on Linux, "libavcodec.62.dylib" on macOS
        foreach (var dir in candidates.Where(d => d.Length > 0 && Directory.Exists(d) && Directory.EnumerateFiles(d, "*avcodec*").Any()))
            if (TryLoad(dir)) return true;
        // Linux: the distribution's FFmpeg, found by the dynamic loader's default search path
        if (OperatingSystem.IsLinux() && TryLoad("")) return true;
        Log.Warn("FFmpeg libraries not found: videos (SceAvPlayer) and MP3/AAC decoding (SceAudiodec) are disabled");
        return false;
    }

    static bool TryLoad(string dir)
    {
        try
        {
            ffmpeg.RootPath = dir;
            uint v = ffmpeg.avcodec_version();
            ffmpeg.av_log_set_level(ffmpeg.AV_LOG_ERROR);
            Log.Info($"FFmpeg avcodec {v >> 16}.{(v >> 8) & 0xFF}.{v & 0xFF} loaded from {(dir.Length > 0 ? dir : "the system library path")}");
            return true;
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException or NotSupportedException)
        {
            if (dir.Length > 0) Log.Warn($"FFmpeg in {dir} could not be loaded: {e.Message}");
            return false;
        }
    }
}
