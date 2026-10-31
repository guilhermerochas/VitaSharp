using VitaSharp.Core.Media;
using Xunit.Abstractions;

namespace VitaSharp.Tests;

/// <summary>
/// SceAudiodec's decoder interface (<see cref="IAudioFrameDecoder"/>, <see cref="AudioFrameDecoders"/>): FFmpeg returns
/// one full frame of PCM per compressed frame (1152/576 samples for MP3, 1024 for AAC), the counts Android's MediaCodec
/// decoder reproduces; and SceMp4's demuxer hook (<see cref="Mp4Demuxers"/>).
/// </summary>
public sealed class AudioFrameDecoderTests(ITestOutputHelper output)
{
    static readonly byte[] Mp3Mpeg1 = Mp3Frame(0xFB, 0x90, 0x00, 417);   // MPEG-1 layer III, 128 kbit/s, 44.1 kHz, stereo
    static readonly byte[] Mp3Mpeg2 = Mp3Frame(0xF3, 0x84, 0xC0, 192);   // MPEG-2 layer III, 64 kbit/s, 24 kHz, mono

    /// <summary>A silent layer III frame: the header, zero side information (no main data) and zero padding.</summary>
    static byte[] Mp3Frame(byte b1, byte b2, byte b3, int length)
    {
        var f = new byte[length];
        f[0] = 0xFF; f[1] = b1; f[2] = b2; f[3] = b3;
        return f;
    }

    [Fact]
    public void Mp3FrameHeaders()
    {
        Assert.Equal(417, FrameDecoder.Mp3FrameLength(Mp3Mpeg1));
        Assert.Equal(192, FrameDecoder.Mp3FrameLength(Mp3Mpeg2));
        Assert.Equal(1152, AudioFrameDecoders.Mp3FrameSamples(Mp3Mpeg1));
        Assert.Equal(576, AudioFrameDecoders.Mp3FrameSamples(Mp3Mpeg2));
        Assert.Equal(0, AudioFrameDecoders.Mp3FrameSamples([0xFF, 0xF1, 0x50, 0x80])); // ADTS, not MP3
    }

    [Fact]
    public unsafe void FFmpegDecodesOneFullFramePerFrame()
    {
        if (!FFmpegLoader.Available) { output.WriteLine("FFmpeg not available: skipped"); return; }
        var pcm = new short[8192];
        int Decode(IAudioFrameDecoder d, byte[] frame, int ch) { fixed (byte* p = frame) return d.Decode(p, frame.Length, pcm, ch); }
        using (var mp3 = AudioFrameDecoders.CreateMp3(2))
        {
            Assert.NotNull(mp3);
            for (int i = 0; i < 4; i++) Assert.Equal(1152, Decode(mp3!, Mp3Mpeg1, 2));
        }
        using (var mp3 = AudioFrameDecoders.CreateMp3(1))
            for (int i = 0; i < 4; i++) Assert.Equal(576, Decode(mp3!, Mp3Mpeg2, 1));

        var packets = RawAacFrameLengthTests.EncodeSine(20, out var config);
        if (packets.Count == 0) { output.WriteLine("no AAC encoder: AAC part skipped"); return; }
        using var aac = AudioFrameDecoders.CreateAac(48000, 2, config);
        Assert.NotNull(aac);
        foreach (var p in packets) Assert.Equal(1024, Decode(aac!, p, 2));
    }

    sealed class FakeDemuxer : IMp4Demuxer
    {
        public IReadOnlyList<DemuxedStream> Streams { get; } = [];
        public long Duration90k => 90000;
        public DemuxedUnit? Next(Func<int, bool> enabled) => null;
        public void Seek(long pts90k) { }
        public void Dispose() { }
    }

    [Fact]
    public void Mp4DemuxerBackendHook()
    {
        var fake = new FakeDemuxer();
        var previous = Mp4Demuxers.Backend;
        Mp4Demuxers.Backend = path => path == "fake:movie.mp4" ? fake : null;
        try
        {
            Assert.Same(fake, Mp4Demuxers.Open("fake:movie.mp4"));
            Assert.Null(Mp4Demuxers.Open(Path.Combine(Path.GetTempPath(), "vitasharp-missing-" + Guid.NewGuid().ToString("N") + ".mp4")));
        }
        finally { Mp4Demuxers.Backend = previous; }
    }
}
