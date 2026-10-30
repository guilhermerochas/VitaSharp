using VitaSharp.Core.Media;

namespace VitaSharp.Android.Media;

/// <summary>The game process's media backends without FFmpeg (SceAudiodec MP3/AAC, SceMp4).</summary>
public static class AndroidMediaBackends
{
    /// <summary>SceAudiodec MP3/AAC through MediaCodec, SceMp4 through MediaExtractor.</summary>
    public static void InstallAudioAndMp4()
    {
        AudioFrameDecoders.Backend = new MediaCodecAudioFrameBackend();
        Mp4Demuxers.Backend = MediaExtractorMp4Demuxer.Open;
    }
}
