using FFmpeg.AutoGen;

namespace VitaSharp.Core.Media;

/// <summary>A decoded picture: YUV 4:2:0 planes copied out of FFmpeg.</summary>
public sealed class DecodedPicture
{
    public int Width, Height;
    public byte[] Y = [], U = [], V = [];
    public long Pts;
    public bool FullRange;
    public bool Bt709;
}

/// <summary>Decodes H.264 access units (Annex B elementary stream) one at a time, as SceAvcdec does.</summary>
public sealed unsafe class H264Decoder : IDisposable
{
    AVCodecContext* _ctx;
    readonly AVPacket* _pkt;
    readonly AVFrame* _frame;
    readonly Queue<DecodedPicture> _ready = new();

    H264Decoder(AVCodecContext* ctx)
    {
        _ctx = ctx;
        _pkt = ffmpeg.av_packet_alloc();
        _frame = ffmpeg.av_frame_alloc();
    }

    public static H264Decoder? Create()
    {
        if (!FFmpegLoader.Available) return null;
        var codec = ffmpeg.avcodec_find_decoder(AVCodecID.AV_CODEC_ID_H264);
        if (codec == null) return null;
        var ctx = ffmpeg.avcodec_alloc_context3(codec);
        ctx->flags |= ffmpeg.AV_CODEC_FLAG_LOW_DELAY; // the hardware decoder returns pictures as soon as possible
        ctx->thread_count = 1;
        if (ffmpeg.avcodec_open2(ctx, codec, null) < 0)
        {
            ffmpeg.avcodec_free_context(&ctx);
            return null;
        }
        return new H264Decoder(ctx);
    }

    /// <summary>Feeds one access unit; decoded pictures become available through <see cref="Take"/>.</summary>
    public void Send(ReadOnlySpan<byte> au, long pts)
    {
        var buf = (byte*)ffmpeg.av_malloc((ulong)au.Length + ffmpeg.AV_INPUT_BUFFER_PADDING_SIZE);
        au.CopyTo(new Span<byte>(buf, au.Length));
        new Span<byte>(buf + au.Length, ffmpeg.AV_INPUT_BUFFER_PADDING_SIZE).Clear();
        if (ffmpeg.av_packet_from_data(_pkt, buf, au.Length) < 0) { ffmpeg.av_free(buf); return; }
        _pkt->pts = pts;
        if (ffmpeg.avcodec_send_packet(_ctx, _pkt) >= 0) Receive();
        ffmpeg.av_packet_unref(_pkt);
    }

    /// <summary>Drains pictures still held for reordering (end of stream).</summary>
    public void Flush()
    {
        ffmpeg.avcodec_send_packet(_ctx, null);
        Receive();
        ffmpeg.avcodec_flush_buffers(_ctx);
    }

    public void Reset()
    {
        ffmpeg.avcodec_flush_buffers(_ctx);
        _ready.Clear();
    }

    public DecodedPicture? Take() => _ready.Count > 0 ? _ready.Dequeue() : null;

    void Receive()
    {
        while (ffmpeg.avcodec_receive_frame(_ctx, _frame) == 0)
        {
            int w = _frame->width, h = _frame->height, cw = (w + 1) / 2, ch = (h + 1) / 2;
            var p = new DecodedPicture
            {
                Width = w, Height = h, Pts = _frame->pts,
                Y = new byte[w * h], U = new byte[cw * ch], V = new byte[cw * ch],
                FullRange = _frame->color_range == AVColorRange.AVCOL_RANGE_JPEG
                            || (AVPixelFormat)_frame->format == AVPixelFormat.AV_PIX_FMT_YUVJ420P,
                Bt709 = _frame->colorspace == AVColorSpace.AVCOL_SPC_BT709,
            };
            Copy(_frame->data[0], _frame->linesize[0], p.Y, w, h);
            if ((AVPixelFormat)_frame->format == AVPixelFormat.AV_PIX_FMT_NV12)
            {
                for (int y = 0; y < ch; y++)
                {
                    byte* row = _frame->data[1] + y * _frame->linesize[1];
                    for (int x = 0; x < cw; x++) { p.U[y * cw + x] = row[2 * x]; p.V[y * cw + x] = row[2 * x + 1]; }
                }
            }
            else
            {
                Copy(_frame->data[1], _frame->linesize[1], p.U, cw, ch);
                Copy(_frame->data[2], _frame->linesize[2], p.V, cw, ch);
            }
            _ready.Enqueue(p);
            ffmpeg.av_frame_unref(_frame);
        }
    }

    static void Copy(byte* src, int stride, byte[] dst, int w, int h)
    {
        fixed (byte* d = dst)
            for (int y = 0; y < h; y++) Buffer.MemoryCopy(src + y * stride, d + y * w, w, w);
    }

    public void Dispose()
    {
        var c = _ctx;
        if (c != null) ffmpeg.avcodec_free_context(&c);
        _ctx = null;
        var p = _pkt; ffmpeg.av_packet_free(&p);
        var f = _frame; ffmpeg.av_frame_free(&f);
    }
}
