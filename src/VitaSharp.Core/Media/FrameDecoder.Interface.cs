namespace VitaSharp.Core.Media;

/// <summary>FFmpeg's <see cref="FrameDecoder"/> as SceAudiodec's <see cref="IAudioFrameDecoder"/> (the desktop backend).</summary>
public sealed unsafe partial class FrameDecoder
{
    public string Name => "ffmpeg";

    int IAudioFrameDecoder.RawFrameLength(ReadOnlySpan<byte> data)
    {
        fixed (byte* p = data) return RawFrameLength(p, data.Length);
    }

    int IAudioFrameDecoder.Decode(ReadOnlySpan<byte> frame, Span<short> pcmOut, int outChannels)
    {
        fixed (byte* p = frame) return Decode(p, frame.Length, pcmOut, outChannels);
    }
}
