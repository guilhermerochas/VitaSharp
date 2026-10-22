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
    const int W = 64, H = 64;

    sealed class Bits
    {
        readonly List<byte> _bytes = new();
        int _cur, _n;
        public void U(int bits, uint v) { for (int i = bits - 1; i >= 0; i--) Bit((int)(v >> i) & 1); }
        public void Bit(int b) { _cur = (_cur << 1) | b; if (++_n == 8) { _bytes.Add((byte)_cur); _cur = _n = 0; } }
        public void Ue(uint v) { uint x = v + 1; int len = 31 - System.Numerics.BitOperations.LeadingZeroCount(x); U(len, 0); U(len + 1, x); }
        public void Se(int v) => Ue(v <= 0 ? (uint)(-2 * v) : (uint)(2 * v - 1));
        public void Align() { while (_n != 0) Bit(0); }
        public void Trailing() { Bit(1); Align(); }
        public byte[] ToArray() => _bytes.ToArray();
    }

    /// <summary>NAL unit with start code and emulation prevention bytes.</summary>
    static byte[] Nal(int refIdc, int type, byte[] rbsp)
    {
        var o = new List<byte> { 0, 0, 0, 1, (byte)((refIdc << 5) | type) };
        int zeros = 0;
        foreach (var b in rbsp)
        {
            if (zeros >= 2 && b <= 3) { o.Add(3); zeros = 0; }
            o.Add(b);
            zeros = b == 0 ? zeros + 1 : 0;
        }
        return o.ToArray();
    }

    static byte[] Sps()
    {
        var b = new Bits();
        b.U(8, 66); b.U(8, 0x40); b.U(8, 30); // constrained baseline (hwaccel rejects plain baseline), level 3.0
        b.Ue(0);                             // sps id
        b.Ue(0);                             // log2_max_frame_num - 4
        b.Ue(2);                             // pic_order_cnt_type 2: output order = decoding order
        b.Ue(1);                             // max_num_ref_frames
        b.Bit(0);                            // gaps_in_frame_num_value_allowed
        b.Ue(W / 16 - 1); b.Ue(H / 16 - 1);
        b.Bit(1); b.Bit(1);                  // frame_mbs_only, direct_8x8_inference
        b.Bit(0); b.Bit(0);                  // no cropping, no VUI
        b.Trailing();
        return Nal(3, 7, b.ToArray());
    }

    static byte[] Pps()
    {
        var b = new Bits();
        b.Ue(0); b.Ue(0);                    // pps id, sps id
        b.Bit(0); b.Bit(0);                  // CAVLC, no bottom field poc
        b.Ue(0); b.Ue(0); b.Ue(0);           // slice groups, ref idx l0/l1
        b.Bit(0); b.U(2, 0);                 // no weighted prediction
        b.Se(0); b.Se(0); b.Se(0);           // qp, qs, chroma qp offset
        b.Bit(1); b.Bit(0); b.Bit(0);        // deblocking control present, constrained intra off, no redundant pic cnt
        b.Trailing();
        return Nal(3, 8, b.ToArray());
    }

    /// <summary>One picture of I_PCM macroblocks with deterministic sample values (never 0, so no start code emulation).</summary>
    static byte[] Picture(int index, out byte[] y, out byte[] u, out byte[] v)
    {
        y = new byte[W * H]; u = new byte[W * H / 4]; v = new byte[W * H / 4];
        for (int i = 0; i < y.Length; i++) y[i] = (byte)(16 + (i * 7 + index * 31) % 220);
        for (int i = 0; i < u.Length; i++) { u[i] = (byte)(16 + (i * 3 + index * 17) % 220); v[i] = (byte)(16 + (i * 5 + index * 13) % 220); }
        bool idr = index == 0;
        var b = new Bits();
        b.Ue(0);                             // first_mb_in_slice
        b.Ue(7);                             // slice_type: I (all slices)
        b.Ue(0);                             // pps id
        b.U(4, (uint)index);                 // frame_num
        if (idr) b.Ue(0);                    // idr_pic_id
        if (idr) { b.Bit(0); b.Bit(0); }     // no_output_of_prior_pics, long_term_reference
        else b.Bit(0);                       // adaptive_ref_pic_marking_mode
        b.Se(0);                             // slice_qp_delta
        b.Ue(1);                             // disable_deblocking_filter_idc = 1
        for (int mb = 0; mb < (W / 16) * (H / 16); mb++)
        {
            int mx = mb % (W / 16), my = mb / (W / 16);
            b.Ue(25);                        // mb_type I_PCM
            b.Align();                       // pcm_alignment_zero_bits
            for (int r = 0; r < 16; r++) for (int c = 0; c < 16; c++) b.U(8, y[(my * 16 + r) * W + mx * 16 + c]);
            for (int r = 0; r < 8; r++) for (int c = 0; c < 8; c++) b.U(8, u[(my * 8 + r) * (W / 2) + mx * 8 + c]);
            for (int r = 0; r < 8; r++) for (int c = 0; c < 8; c++) b.U(8, v[(my * 8 + r) * (W / 2) + mx * 8 + c]);
        }
        b.Trailing();
        var slice = Nal(3, idr ? 5 : 1, b.ToArray());
        return idr ? [.. Sps(), .. Pps(), .. slice] : slice;
    }

    static List<(byte[] Au, byte[] Y, byte[] U, byte[] V)> Stream(int pictures)
    {
        var list = new List<(byte[], byte[], byte[], byte[])>();
        for (int i = 0; i < pictures; i++) list.Add((Picture(i, out var y, out var u, out var v), y, u, v));
        return list;
    }

    /// <summary>Decodes the stream (optionally failing the hardware decoder before picture <paramref name="failAt"/>).</summary>
    static List<DecodedPicture> Decode(IVideoDecoder d, List<(byte[] Au, byte[] Y, byte[] U, byte[] V)> stream, int failAt = -1)
    {
        var got = new List<DecodedPicture>();
        for (int i = 0; i < stream.Count; i++)
        {
            if (i == failAt && d is FFmpegVideoDecoder f) f.SimulateHardwareFailure();
            d.Send(stream[i].Au, i);
            while (d.Take() is { } p) got.Add(Copy(p));
        }
        d.Flush();
        while (d.Take() is { } p) got.Add(Copy(p));
        return got;
    }

    static DecodedPicture Copy(DecodedPicture p) => new() { Width = p.Width, Height = p.Height, Pts = p.Pts, Y = p.Y.ToArray(), U = p.U.ToArray(), V = p.V.ToArray() };

    static void AssertExact(List<(byte[] Au, byte[] Y, byte[] U, byte[] V)> stream, List<DecodedPicture> got)
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
        var s = Stream(2);
        Assert.True(FFmpegVideoDecoder.ContainsIdr(s[0].Au));
        Assert.False(FFmpegVideoDecoder.ContainsIdr(s[1].Au));
    }

    [Fact]
    public void SoftwareDecoderIsBitExact()
    {
        if (!FFmpegLoader.Available) { output.WriteLine("FFmpeg not available: skipped"); return; }
        using var d = FFmpegVideoDecoder.Create(AVCodecID.AV_CODEC_ID_H264, AVHWDeviceType.AV_HWDEVICE_TYPE_NONE)!;
        Assert.False(d.IsHardware);
        var s = Stream(6);
        AssertExact(s, Decode(d, s));
    }

    [Fact]
    public void HardwareDecoderMatchesOrFallsBack()
    {
        if (!FFmpegLoader.Available) { output.WriteLine("FFmpeg not available: skipped"); return; }
        var s = Stream(6);
        using (var d = FFmpegVideoDecoder.Create(AVCodecID.AV_CODEC_ID_H264, FFmpegVideoBackend.PlatformDevice)!)
        {
            long hw0 = FFmpegVideoDecoder.HardwarePictures;
            output.WriteLine($"decoder: {d.Name}");
            AssertExact(s, Decode(d, s)); // hardware, or software when this host has no usable decoder
            output.WriteLine($"pictures decoded in hardware: {FFmpegVideoDecoder.HardwarePictures - hw0} of {s.Count}");
        }
        // a hardware failure mid-stream continues in software from the last IDR without losing pictures
        using (var d = FFmpegVideoDecoder.Create(AVCodecID.AV_CODEC_ID_H264, FFmpegVideoBackend.PlatformDevice)!)
        {
            bool hw = d.IsHardware;
            var got = Decode(d, s, failAt: 3);
            output.WriteLine($"fallback test started on {(hw ? "hardware" : "software")}, ended on {d.Name}");
            Assert.False(d.IsHardware);
            AssertExact(s, got);
        }
    }

    /// <summary>Muxes the Annex B test stream into an MP4 file (no encoding: FFmpeg's mov muxer writes the avcC itself).</summary>
    static unsafe void WriteMp4(string path, List<(byte[] Au, byte[] Y, byte[] U, byte[] V)> stream)
    {
        AVFormatContext* oc = null;
        Assert.True(ffmpeg.avformat_alloc_output_context2(&oc, null, "mp4", path) >= 0);
        var st = ffmpeg.avformat_new_stream(oc, null);
        st->time_base = new AVRational { num = 1, den = 30 };
        var par = st->codecpar;
        par->codec_type = AVMediaType.AVMEDIA_TYPE_VIDEO;
        par->codec_id = AVCodecID.AV_CODEC_ID_H264;
        par->width = W;
        par->height = H;
        byte[] header = [.. Sps(), .. Pps()];
        par->extradata = (byte*)ffmpeg.av_mallocz((ulong)header.Length + ffmpeg.AV_INPUT_BUFFER_PADDING_SIZE);
        header.CopyTo(new Span<byte>(par->extradata, header.Length));
        par->extradata_size = header.Length;
        Assert.True(ffmpeg.avio_open(&oc->pb, path, ffmpeg.AVIO_FLAG_WRITE) >= 0);
        Assert.True(ffmpeg.avformat_write_header(oc, null) >= 0);
        var pkt = ffmpeg.av_packet_alloc();
        for (int i = 0; i < stream.Count; i++)
        {
            var au = stream[i].Au;
            ffmpeg.av_new_packet(pkt, au.Length);
            au.CopyTo(new Span<byte>(pkt->data, au.Length));
            pkt->pts = pkt->dts = ffmpeg.av_rescale_q(i, new AVRational { num = 1, den = 30 }, st->time_base);
            pkt->duration = ffmpeg.av_rescale_q(1, new AVRational { num = 1, den = 30 }, st->time_base);
            pkt->stream_index = 0;
            if (i == 0) pkt->flags |= ffmpeg.AV_PKT_FLAG_KEY;
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
        if (!FFmpegLoader.Available) { output.WriteLine("FFmpeg not available: skipped"); return; }
        var s = Stream(8);
        string path = Path.Combine(Path.GetTempPath(), $"vitasharp-videotest-{Environment.ProcessId}-{mode}.mp4");
        var oldBackend = VideoDecoders.Backend;
        var backend = new FFmpegVideoBackend();
        backend.Configure(mode);
        VideoDecoders.Backend = backend;
        try
        {
            WriteMp4(path, s);
            using var player = MediaPlayer.Open(path)!;
            Assert.NotNull(player);
            Assert.Equal((W, H), (player.Width, player.Height));
            var frames = new List<VideoFrame>();
            var until = DateTime.UtcNow.AddSeconds(20);
            // the playback clock advances one frame at a time (TakeVideo drops frames that are already late)
            while (frames.Count < s.Count && DateTime.UtcNow < until)
            {
                long clock = (long)(frames.Count * 1_000_000.0 / 30) + 1000;
                if (player.TakeVideo(clock) is { } f) frames.Add(f with { Nv12 = f.Nv12.ToArray() });
                else Thread.Sleep(2);
            }
            Assert.Equal(s.Count, frames.Count);
            foreach (var f in frames)
            {
                int i = (int)Math.Round(f.TimeUs * 30 / 1_000_000.0);
                Assert.InRange(i, 0, s.Count - 1);
                Assert.Equal(s[i].Y, f.Nv12.AsSpan(0, W * H).ToArray());
                var vu = new byte[W * H / 2];
                for (int k = 0; k < W * H / 4; k++) { vu[2 * k] = s[i].V[k]; vu[2 * k + 1] = s[i].U[k]; }
                Assert.Equal(vu, f.Nv12.AsSpan(W * H).ToArray());
            }
            output.WriteLine($"{mode}: {frames.Count} frames compared");
        }
        finally
        {
            VideoDecoders.Backend = oldBackend;
            try { File.Delete(path); } catch (IOException) { }
        }
    }

    [Fact]
    public void BackendHonoursSoftwareSetting()
    {
        if (!FFmpegLoader.Available) { output.WriteLine("FFmpeg not available: skipped"); return; }
        var backend = new FFmpegVideoBackend();
        backend.Configure("sw");
        using var d = backend.CreateH264(new VideoDecoderOptions(W, H))!;
        Assert.Equal("ffmpeg-sw", d.Name);
    }
}
