using FFmpeg.AutoGen;
using VitaSharp.Core.Media;
using Xunit.Abstractions;

namespace VitaSharp.Tests;

/// <summary>
/// The pluggable H.264 decoder: a hand-built baseline stream of I_PCM macroblocks (lossless, deblocking off) must come
/// out bit-exact from the software decoder, from the hardware (hwaccel) decoder when this machine has one, and across
/// a hardware-to-software fallback in the middle of the stream.
/// </summary>
public sealed class VideoDecoderTests(ITestOutputHelper output)
{
    internal const int W = 64, H = 64;

    /// <summary>The shared synthetic stream (<see cref="SyntheticH264"/>) as (access unit, Y, U, V).</summary>
    internal static List<(byte[] Au, byte[] Y, byte[] U, byte[] V)> Stream(int pictures) =>
        SyntheticH264.Stream(pictures, W, H).Select(unit => (unit.AccessUnit, unit.Y, unit.U, unit.V)).ToList();

    /// <summary>Decodes the stream (optionally failing the hardware decoder before picture <paramref name="failAt"/>).</summary>
    private static List<DecodedPicture> Decode(IVideoDecoder d, List<(byte[] Au, byte[] Y, byte[] U, byte[] V)> stream, int failAt = -1)
    {
        var got = new List<DecodedPicture>();
        for (int i = 0; i < stream.Count; i++)
        {
            if (i == failAt && d is FFmpegVideoDecoder f)
            {
                f.SimulateHardwareFailure();
            }

            d.Send(stream[i].Au, i);
            while (d.Take() is { } p)
            {
                got.Add(Copy(p));
            }
        }
        d.Flush();
        while (d.Take() is { } p)
        {
            got.Add(Copy(p));
        }

        return got;
    }

    private static DecodedPicture Copy(DecodedPicture p) => new() { Width = p.Width, Height = p.Height, Pts = p.Pts, Y = p.Y.ToArray(), U = p.U.ToArray(), V = p.V.ToArray() };

    private static void AssertExact(List<(byte[] Au, byte[] Y, byte[] U, byte[] V)> stream, List<DecodedPicture> got)
    {
        Assert.Equal(stream.Count, got.Count);
        for (int i = 0; i < got.Count; i++)
        {
            Assert.Equal((W, H), (got[i].Width, got[i].Height));
            Assert.Equal(i, got[i].Pts);
            Assert.Equal(stream[i].Y, got[i].Y);
            Assert.Equal(stream[i].U, got[i].U);
            Assert.Equal(stream[i].V, got[i].V);
        }
    }

    [Fact]
    public void IdrDetection()
    {
        List<(byte[] Au, byte[] Y, byte[] U, byte[] V)> s = Stream(2);
        Assert.True(FFmpegVideoDecoder.ContainsIdr(s[0].Au));
        Assert.False(FFmpegVideoDecoder.ContainsIdr(s[1].Au));
    }

    [Fact]
    public void SoftwareDecoderIsBitExact()
    {
        if (!FFmpegLoader.Available)
        {
            output.WriteLine("FFmpeg not available: skipped");
            return;
        }
        using FFmpegVideoDecoder d = FFmpegVideoDecoder.Create(AVCodecID.AV_CODEC_ID_H264, AVHWDeviceType.AV_HWDEVICE_TYPE_NONE)!;
        Assert.False(d.IsHardware);
        List<(byte[] Au, byte[] Y, byte[] U, byte[] V)> s = Stream(6);
        AssertExact(s, Decode(d, s));
    }

    [Fact]
    public void HardwareDecoderMatchesOrFallsBack()
    {
        if (!FFmpegLoader.Available)
        {
            output.WriteLine("FFmpeg not available: skipped");
            return;
        }
        List<(byte[] Au, byte[] Y, byte[] U, byte[] V)> s = Stream(6);
        using (FFmpegVideoDecoder d = FFmpegVideoDecoder.Create(AVCodecID.AV_CODEC_ID_H264, FFmpegVideoBackend.PlatformDevice)!)
        {
            long hw0 = FFmpegVideoDecoder.HardwarePictures;
            output.WriteLine($"decoder: {d.Name}");
            AssertExact(s, Decode(d, s)); // hardware, or software when this host has no usable decoder
            output.WriteLine($"pictures decoded in hardware: {FFmpegVideoDecoder.HardwarePictures - hw0} of {s.Count}");
        }
        // a hardware failure mid-stream continues in software from the last IDR without losing pictures
        using (FFmpegVideoDecoder d = FFmpegVideoDecoder.Create(AVCodecID.AV_CODEC_ID_H264, FFmpegVideoBackend.PlatformDevice)!)
        {
            bool hw = d.IsHardware;
            List<DecodedPicture> got = Decode(d, s, failAt: 3);
            output.WriteLine($"fallback test started on {(hw ? "hardware" : "software")}, ended on {d.Name}");
            Assert.False(d.IsHardware);
            AssertExact(s, got);
        }
    }

    /// <summary>Muxes the Annex B test stream into an MP4 file (no encoding: FFmpeg's mov muxer writes the avcC itself).</summary>
    private static unsafe void WriteMp4(string path, List<(byte[] Au, byte[] Y, byte[] U, byte[] V)> stream)
    {
        AVFormatContext* oc = null;
        Assert.True(ffmpeg.avformat_alloc_output_context2(&oc, null, "mp4", path) >= 0);
        AVStream* st = ffmpeg.avformat_new_stream(oc, null);
        st->time_base = new AVRational { num = 1, den = 30 };
        AVCodecParameters* par = st->codecpar;
        par->codec_type = AVMediaType.AVMEDIA_TYPE_VIDEO;
        par->codec_id = AVCodecID.AV_CODEC_ID_H264;
        par->width = W;
        par->height = H;
        byte[] header = SyntheticH264.ParameterSets(W, H);
        par->extradata = (byte*)ffmpeg.av_mallocz((ulong)header.Length + ffmpeg.AV_INPUT_BUFFER_PADDING_SIZE);
        header.CopyTo(new Span<byte>(par->extradata, header.Length));
        par->extradata_size = header.Length;
        Assert.True(ffmpeg.avio_open(&oc->pb, path, ffmpeg.AVIO_FLAG_WRITE) >= 0);
        Assert.True(ffmpeg.avformat_write_header(oc, null) >= 0);
        AVPacket* pkt = ffmpeg.av_packet_alloc();
        for (int i = 0; i < stream.Count; i++)
        {
            byte[] au = stream[i].Au;
            ffmpeg.av_new_packet(pkt, au.Length);
            au.CopyTo(new Span<byte>(pkt->data, au.Length));
            pkt->pts = pkt->dts = ffmpeg.av_rescale_q(i, new AVRational { num = 1, den = 30 }, st->time_base);
            pkt->duration = ffmpeg.av_rescale_q(1, new AVRational { num = 1, den = 30 }, st->time_base);
            pkt->stream_index = 0;
            if (i == 0)
            {
                pkt->flags |= ffmpeg.AV_PKT_FLAG_KEY;
            }

            Assert.True(ffmpeg.av_interleaved_write_frame(oc, pkt) >= 0);
        }
        ffmpeg.av_packet_free(&pkt);
        ffmpeg.av_write_trailer(oc);
        ffmpeg.avio_closep(&oc->pb);
        ffmpeg.avformat_free_context(oc);
    }

    /// <summary>SceAvPlayer's media player decodes MP4 H.264 through the pluggable decoder into exact NV21 frames.</summary>
    [Theory]
    [InlineData("sw")]
    [InlineData("auto")]
    public void MediaPlayerDecodesMp4ThroughTheDecoderInterface(string mode)
    {
        if (!FFmpegLoader.Available)
        {
            output.WriteLine("FFmpeg not available: skipped");
            return;
        }
        List<(byte[] Au, byte[] Y, byte[] U, byte[] V)> s = Stream(8);
        string path = Path.Combine(Path.GetTempPath(), $"vitasharp-videotest-{Environment.ProcessId}-{mode}.mp4");
        IVideoDecoderBackend oldBackend = VideoDecoders.Backend;
        var backend = new FFmpegVideoBackend();
        backend.Configure(mode);
        VideoDecoders.Backend = backend;
        try
        {
            WriteMp4(path, s);
            using MediaPlayer player = MediaPlayer.Open(path)!;
            Assert.NotNull(player);
            Assert.Equal((W, H), (player.Width, player.Height));
            var frames = new List<VideoFrame>();
            DateTime until = DateTime.UtcNow.AddSeconds(20);
            // the playback clock advances one frame at a time (TakeVideo drops frames that are already late)
            while (frames.Count < s.Count && DateTime.UtcNow < until)
            {
                long clock = (long)(frames.Count * 1_000_000.0 / 30) + 1000;
                if (player.TakeVideo(clock) is { } f)
                {
                    frames.Add(f with
                    {
                        Nv12 = f.Nv12.ToArray()
                    });
                }
                else
                {
                    Thread.Sleep(2);
                }
            }
            Assert.Equal(s.Count, frames.Count);
            foreach (VideoFrame f in frames)
            {
                int i = (int)Math.Round(f.TimeUs * 30 / 1_000_000.0);
                Assert.InRange(i, 0, s.Count - 1);
                Assert.Equal(s[i].Y, f.Nv12.AsSpan(0, W * H).ToArray());
                byte[] vu = new byte[W * H / 2];
                for (int k = 0; k < W * H / 4; k++)
                {
                    vu[2 * k] = s[i].V[k];
                    vu[2 * k + 1] = s[i].U[k];
                }
                Assert.Equal(vu, f.Nv12.AsSpan(W * H).ToArray());
            }
            output.WriteLine($"{mode}: {frames.Count} frames compared");
        }
        finally
        {
            VideoDecoders.Backend = oldBackend;
            try
            {
                File.Delete(path);
            }
            catch (IOException) { }
        }
    }

    [Fact]
    public void TempMoviesOfEndedSessionsAreDeleted()
    {
        string stale = Path.Combine(Path.GetTempPath(), $"vitasharp-mp4-2147483600-{Guid.NewGuid():N}.mp4");
        string own = TempMovies.NewPath("mp4", Guid.NewGuid().ToString("N"));
        File.WriteAllBytes(stale, [1]);
        File.WriteAllBytes(own, [1]);
        try
        {
            TempMovies.SweepStale();
            Assert.False(File.Exists(stale));
            Assert.True(File.Exists(own));
        }
        finally
        {
            File.Delete(stale);
            File.Delete(own);
        }
    }

    [Fact]
    public void BackendHonoursSoftwareSetting()
    {
        if (!FFmpegLoader.Available)
        {
            output.WriteLine("FFmpeg not available: skipped");
            return;
        }
        var backend = new FFmpegVideoBackend();
        backend.Configure("sw");
        using IVideoDecoder d = backend.CreateH264(new VideoDecoderOptions(W, H))!;
        Assert.Equal("ffmpeg-sw", d.Name);
        backend.Configure("auto"); // software unless hardware is asked for
        using IVideoDecoder a = backend.CreateH264(new VideoDecoderOptions(W, H))!;
        Assert.Equal("ffmpeg-sw", a.Name);
    }
}
