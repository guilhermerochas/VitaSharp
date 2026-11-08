using FFmpeg.AutoGen;
using VitaSharp.Core.Util;

namespace VitaSharp.Core.Media;

/// <summary>
/// An H.264 encoder for SceAvcenc (the Camera's video recording): YUV 4:2:0 planar pictures in, Annex B access units
/// out (one per picture, no B-frames, parameter sets in band before each IDR). It is FFmpeg's encoder: Cisco's
/// OpenH264 (BSD, in the LGPL FFmpeg build the emulator ships) first, then whatever other H.264 encoder that FFmpeg
/// has. Null when FFmpeg or every encoder is missing: the caller then produces no pictures.
/// </summary>
public sealed unsafe class H264Encoder : IDisposable
{
    AVCodecContext* _ctx;
    AVFrame* _frame;
    AVPacket* _pkt;
    long _pts;

    public int Width { get; }
    public int Height { get; }
    public int FrameRate { get; }
    public string Name { get; }

    H264Encoder(AVCodecContext* ctx, int w, int h, int fps, string name)
    {
        _ctx = ctx;
        Width = w;
        Height = h;
        FrameRate = fps;
        Name = name;
        _frame = ffmpeg.av_frame_alloc();
        _frame->format = (int)AVPixelFormat.AV_PIX_FMT_YUV420P;
        _frame->width = w;
        _frame->height = h;
        ffmpeg.av_frame_get_buffer(_frame, 32);
        _pkt = ffmpeg.av_packet_alloc();
    }

    public static H264Encoder? TryCreate(int width, int height, int fps, int bitRate, int gop)
    {
        if (width <= 0 || height <= 0 || (width & 1) != 0 || (height & 1) != 0 || !FFmpegLoader.Available) return null;
        fps = Math.Clamp(fps, 1, 120);
        foreach (var name in new[] { "libopenh264", "" })
        {
            var codec = name.Length > 0 ? ffmpeg.avcodec_find_encoder_by_name(name) : ffmpeg.avcodec_find_encoder(AVCodecID.AV_CODEC_ID_H264);
            if (codec == null) continue;
            var ctx = ffmpeg.avcodec_alloc_context3(codec);
            ctx->width = width;
            ctx->height = height;
            ctx->pix_fmt = AVPixelFormat.AV_PIX_FMT_YUV420P;
            ctx->time_base = new AVRational { num = 1, den = fps };
            ctx->framerate = new AVRational { num = fps, den = 1 };
            ctx->gop_size = Math.Max(gop, 1);
            ctx->max_b_frames = 0;
            ctx->bit_rate = bitRate > 0 ? bitRate : 2_000_000;
            ctx->thread_count = 1;
            ctx->flags |= ffmpeg.AV_CODEC_FLAG_LOW_DELAY;
            if (ffmpeg.avcodec_open2(ctx, codec, null) < 0)
            {
                ffmpeg.avcodec_free_context(&ctx);
                continue;
            }
            string n = System.Runtime.InteropServices.Marshal.PtrToStringAnsi((IntPtr)codec->name) ?? "?";
            Log.Info($"H.264 encoder: {n} {width}x{height} {fps} fps {ctx->bit_rate / 1000} kbit/s, GOP {ctx->gop_size}");
            return new H264Encoder(ctx, width, height, fps, n);
        }
        Log.WarnOnce("h264enc-none", "No H.264 encoder in FFmpeg: video recording produces no pictures");
        return null;
    }

    /// <summary>Encodes one picture (planes read through the given row pitches) and returns the access units the
    /// encoder had ready (usually exactly this picture's), each with whether it is a key picture.</summary>
    public List<(byte[] Data, bool Key)> Encode(ReadOnlySpan<byte> y, int yPitch, ReadOnlySpan<byte> u, ReadOnlySpan<byte> v, int cPitch)
    {
        ffmpeg.av_frame_make_writable(_frame);
        Copy(y, yPitch, _frame->data[0], _frame->linesize[0], Width, Height);
        Copy(u, cPitch, _frame->data[1], _frame->linesize[1], Width / 2, Height / 2);
        Copy(v, cPitch, _frame->data[2], _frame->linesize[2], Width / 2, Height / 2);
        _frame->pts = _pts++;
        var list = new List<(byte[], bool)>();
        if (ffmpeg.avcodec_send_frame(_ctx, _frame) >= 0) Drain(list);
        return list;
    }

    /// <summary>The access units still held by the encoder (end of the stream).</summary>
    public List<(byte[] Data, bool Key)> Flush()
    {
        var list = new List<(byte[], bool)>();
        if (ffmpeg.avcodec_send_frame(_ctx, null) >= 0) Drain(list);
        return list;
    }

    void Drain(List<(byte[], bool)> list)
    {
        while (ffmpeg.avcodec_receive_packet(_ctx, _pkt) >= 0)
        {
            list.Add((new ReadOnlySpan<byte>(_pkt->data, _pkt->size).ToArray(), (_pkt->flags & ffmpeg.AV_PKT_FLAG_KEY) != 0));
            ffmpeg.av_packet_unref(_pkt);
        }
    }

    static void Copy(ReadOnlySpan<byte> src, int srcPitch, byte* dst, int dstPitch, int w, int h)
    {
        for (int row = 0; row < h; row++)
        {
            int o = row * srcPitch;
            if (o + w > src.Length) break;
            src.Slice(o, w).CopyTo(new Span<byte>(dst + row * dstPitch, w));
        }
    }

    /// <summary>The NAL units of an Annex B access unit: offset and length of each payload (after its start code).</summary>
    public static List<(int Offset, int Length)> SplitNals(ReadOnlySpan<byte> au)
    {
        var starts = new List<int>();
        for (int i = 0; i + 2 < au.Length; i++)
            if (au[i] == 0 && au[i + 1] == 0 && au[i + 2] == 1) { starts.Add(i + 3); i += 2; }
        var nals = new List<(int, int)>();
        for (int k = 0; k < starts.Count; k++)
        {
            int end = k + 1 < starts.Count ? starts[k + 1] - 3 : au.Length;
            while (end > starts[k] && au[end - 1] == 0) end--; // the next start code's leading zero (4-byte form)
            if (end > starts[k]) nals.Add((starts[k], end - starts[k]));
        }
        return nals;
    }

    public void Dispose()
    {
        if (_ctx == null) return;
        var c = _ctx; ffmpeg.avcodec_free_context(&c); _ctx = null;
        var f = _frame; ffmpeg.av_frame_free(&f); _frame = null;
        var p = _pkt; ffmpeg.av_packet_free(&p); _pkt = null;
    }
}
