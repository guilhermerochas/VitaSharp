using VitaSharp.Core.Media;
using Xunit.Abstractions;

namespace VitaSharp.Tests;

/// <summary>
/// SceAudiodec's decoder interface (<see cref="IAudioFrameDecoder"/>): FFmpeg's backend returns one full frame of PCM
/// per compressed frame (1152/576 for MP3, 1024 for AAC), and <see cref="FrameAlignedAudioDecoder"/> (Android's
/// MediaCodec path) produces the same counts from a decoder with start-up delay, latency and arbitrary buffer sizes
/// (here a fake). Also the ADTS header that path wraps raw AAC in, checked against FFmpeg, and SceMp4's backend hook.
/// </summary>
public sealed class AudioFrameDecoderTests(ITestOutputHelper output)
{
    /// <summary>A MediaCodec-like decoder: the samples of stream S[n] = n (per channel, mod 30000) come out
    /// <paramref name="lag"/> inputs late, without the first <paramref name="trim"/> samples, in
    /// <paramref name="chunk"/>-sample buffers, with <paramref name="channels"/> channels.</summary>
    sealed class FakeCodec(int frameSamples, int lag, int trim, int chunk, int channels, int outputRate = 0) : IAsyncAudioCodec
    {
        public readonly List<byte[]> Inputs = new();
        int _released; // samples per channel handed out (before trimming)
        public bool Flushed, Disposed;
        public string Name => "fake";
        public int OutputSampleRate { get; set; }

        public bool Queue(ReadOnlySpan<byte> frame, PcmFifo output)
        {
            Inputs.Add(frame.ToArray());
            return true;
        }

        public int Drain(PcmFifo output, int waitUs)
        {
            if (outputRate > 0) OutputSampleRate = outputRate;
            int available = Math.Max(0, Inputs.Count - lag) * frameSamples;
            int added = 0;
            while (_released < available)
            {
                int n = Math.Min(chunk, available - _released);
                int from = Math.Max(_released, trim);
                if (from < _released + n)
                {
                    var buf = new short[(_released + n - from) * channels];
                    for (int i = 0; i < buf.Length / channels; i++)
                        for (int c = 0; c < channels; c++) buf[i * channels + c] = (short)((from + i) % 30000 * (c + 1) % 30000);
                    output.Append(buf, channels);
                    added += buf.Length / channels;
                }
                _released += n;
            }
            return added;
        }

        public void Flush() { Flushed = true; Inputs.Clear(); _released = 0; }
        public void Dispose() => Disposed = true;
    }

    static readonly byte[] Mp3Mpeg1 = Mp3Frame(0xFB, 0x90, 0x00, 417);   // MPEG-1 layer III, 128 kbit/s, 44.1 kHz, stereo
    static readonly byte[] Mp3Mpeg2 = Mp3Frame(0xF3, 0x84, 0xC0, 192);   // MPEG-2 layer III, 64 kbit/s, 24 kHz, mono

    /// <summary>A silent layer III frame: the header, zero side information (no main data) and zero padding.</summary>
    static byte[] Mp3Frame(byte b1, byte b2, byte b3, int length)
    {
        var f = new byte[length];
        f[0] = 0xFF; f[1] = b1; f[2] = b2; f[3] = b3;
        return f;
    }

    [Theory]
    [InlineData(0, 0, 4096, 2)]     // an ideal decoder
    [InlineData(1, 0, 1024, 2)]     // one frame of latency
    [InlineData(0, 529, 576, 1)]    // Android's MP3 decoder: the first 529 samples dropped
    [InlineData(2, 529, 333, 2)]    // both, odd buffer sizes
    public void FrameAlignedDecoderReturnsOneFramePerFrame(int lag, int trim, int chunk, int codecChannels)
    {
        const int F = 1152, Calls = 12;
        var codec = new FakeCodec(F, lag, trim, chunk, codecChannels);
        using var d = new FrameAlignedAudioDecoder(codec, new AudioFrameDecoderOptions(AudioFrameCodec.Mp3, 0, 2));
        Assert.Equal(1152, AudioFrames.Mp3FrameSamples(Mp3Mpeg1));
        Assert.Equal(417, AudioFrames.Mp3FrameLength(Mp3Mpeg1));
        var all = new List<short>();
        var pcm = new short[4096];
        for (int i = 0; i < Calls; i++)
        {
            Assert.Equal(F, d.Decode(Mp3Mpeg1, pcm, 2));
            all.AddRange(pcm.AsSpan(0, F * 2).ToArray());
        }
        Assert.Equal(Calls, codec.Inputs.Count);
        Assert.True(codec.Inputs.All(x => x.SequenceEqual(Mp3Mpeg1))); // MP3 frames go in as they are
        // the stream comes out a constant lag * F samples late; the trimmed start is silence (as FFmpeg's start-up
        // samples would be); mono output of the codec is duplicated into both channels
        int shift = lag * F;
        for (int n = 0; n < Calls * F; n++)
        {
            int src = n - shift;
            for (int c = 0; c < 2; c++)
            {
                int cc = Math.Min(c, codecChannels - 1);
                short want = src < trim ? (short)0 : (short)(src % 30000 * (cc + 1) % 30000);
                Assert.True(want == all[n * 2 + c], $"sample {n} ch {c}: {all[n * 2 + c]}, expected {want}");
            }
        }
        d.Flush();
        Assert.True(codec.Flushed);
        Assert.Equal(F, d.Decode(Mp3Mpeg1, pcm, 2));
    }

    [Fact]
    public void FrameAlignedDecoderFrameSizes()
    {
        var pcm = new short[8192];
        // MPEG-2 layer III: 576 samples
        using (var d = new FrameAlignedAudioDecoder(new FakeCodec(576, 0, 0, 576, 1), new AudioFrameDecoderOptions(AudioFrameCodec.Mp3, 0, 1)))
            Assert.Equal(576, d.Decode(Mp3Mpeg2, pcm, 1));
        // AAC 1024, declared SBR 2048, implicit SBR found from the decoder's output rate
        var raw = new byte[] { 0x21, 0x10, 0x05, 0x00 };
        using (var d = new FrameAlignedAudioDecoder(new FakeCodec(1024, 0, 0, 1024, 2), new AudioFrameDecoderOptions(AudioFrameCodec.Aac, 48000, 2, AudioFrames.AacConfig(48000, 2))))
            Assert.Equal(1024, d.Decode(raw, pcm, 2));
        using (var d = new FrameAlignedAudioDecoder(new FakeCodec(2048, 0, 0, 2048, 2), new AudioFrameDecoderOptions(AudioFrameCodec.Aac, 48000, 2, AudioFrames.AacConfig(24000, 2), Sbr: true)))
            Assert.Equal(2048, d.Decode(raw, pcm, 2));
        using (var d = new FrameAlignedAudioDecoder(new FakeCodec(2048, 0, 0, 2048, 2, outputRate: 48000), new AudioFrameDecoderOptions(AudioFrameCodec.Aac, 24000, 2, AudioFrames.AacConfig(24000, 2))))
        {
            d.Decode(raw, pcm, 2); // the first output reports 48 kHz from a 24 kHz core
            Assert.Equal(2048, d.Decode(raw, pcm, 2));
        }
        // a pcm buffer smaller than a frame is filled, not overrun
        using (var d = new FrameAlignedAudioDecoder(new FakeCodec(1152, 0, 0, 1152, 2), new AudioFrameDecoderOptions(AudioFrameCodec.Mp3, 0, 2)))
            Assert.Equal(500, d.Decode(Mp3Mpeg1, new short[1000], 2));
    }

    [Fact]
    public void RawAacIsWrappedInAnAdtsHeader()
    {
        var codec = new FakeCodec(1024, 0, 0, 1024, 2);
        var options = new AudioFrameDecoderOptions(AudioFrameCodec.Aac, 48000, 2, AudioFrames.AacConfig(48000, 2));
        using var d = new FrameAlignedAudioDecoder(codec, options);
        var slot = new byte[1536];
        new Random(3).NextBytes(slot);
        // the codec cannot measure a raw frame: the whole buffer is one ADTS frame
        Assert.Equal(slot.Length, d.RawFrameLength(slot));
        d.Decode(slot, new short[4096], 2);
        var input = Assert.Single(codec.Inputs);
        Assert.Equal(slot.Length + 7, AudioFrames.AdtsFrameLength(input));
        Assert.Equal(48000, AudioFrames.AdtsSampleRate(input));
        Assert.Equal((2, 3, 2), AudioFrames.ParseAacConfig(AudioFrames.AacConfig(48000, 2))!.Value);
        Assert.True(input.AsSpan(7).SequenceEqual(slot));
        // ADTS input passes through unchanged
        var adts = new FakeCodec(1024, 0, 0, 1024, 2);
        using var d2 = new FrameAlignedAudioDecoder(adts, options with { AacConfig = null, Adts = true });
        d2.Decode(input, new short[4096], 2);
        Assert.True(Assert.Single(adts.Inputs).SequenceEqual(input));
    }

    [Fact]
    public void FFmpegBackendDecodesFullFrames()
    {
        if (!FFmpegLoader.Available) { output.WriteLine("FFmpeg not available: skipped"); return; }
        var backend = new FFmpegAudioFrameBackend();
        var pcm = new short[8192];
        using (var mp3 = backend.Create(new AudioFrameDecoderOptions(AudioFrameCodec.Mp3, 0, 2)))
        {
            Assert.NotNull(mp3);
            for (int i = 0; i < 4; i++) Assert.Equal(1152, mp3!.Decode(Mp3Mpeg1, pcm, 2));
        }
        using (var mp3 = backend.Create(new AudioFrameDecoderOptions(AudioFrameCodec.Mp3, 0, 1)))
            for (int i = 0; i < 4; i++) Assert.Equal(576, mp3!.Decode(Mp3Mpeg2, pcm, 1));

        var packets = RawAacFrameLengthTests.EncodeSine(20, out var config);
        if (packets.Count == 0) { output.WriteLine("no AAC encoder: AAC part skipped"); return; }
        var asc = AudioFrames.ParseAacConfig(config);
        Assert.NotNull(asc);
        using var raw = backend.Create(new AudioFrameDecoderOptions(AudioFrameCodec.Aac, 48000, 2, config));
        using var adts = backend.Create(new AudioFrameDecoderOptions(AudioFrameCodec.Aac, 48000, 2, Adts: true));
        Assert.NotNull(raw);
        Assert.NotNull(adts);
        var pcm2 = new short[8192];
        var frame = new byte[8192];
        foreach (var p in packets)
        {
            Assert.Equal(1024, raw!.Decode(p, pcm, 2));
            // the ADTS header FrameAlignedAudioDecoder puts on raw frames for MediaCodec: FFmpeg decodes it to the same PCM
            AudioFrames.WriteAdtsHeader(frame, asc!.Value.ObjectType, asc.Value.RateIndex, asc.Value.Channels, p.Length);
            p.CopyTo(frame, 7);
            Assert.Equal(p.Length + 7, AudioFrames.AdtsFrameLength(frame));
            Assert.Equal(1024, adts!.Decode(frame.AsSpan(0, p.Length + 7), pcm2, 2));
            Assert.True(pcm.AsSpan(0, 2048).SequenceEqual(pcm2.AsSpan(0, 2048)));
        }
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
        Assert.Equal(90000, Mp4Demuxers.UsTo90k(1_000_000));
        Assert.Equal(1_000_000, Mp4Demuxers.Ticks90kToUs(90000));
    }
}
