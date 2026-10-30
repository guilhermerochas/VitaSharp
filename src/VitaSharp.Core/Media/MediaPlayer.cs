using FFmpeg.AutoGen;
using VitaSharp.Core.Util;

namespace VitaSharp.Core.Media;

/// <summary>A decoded video frame in YVU420P2 (NV21) layout: Y plane, then interleaved V/U at half resolution.</summary>
public sealed record VideoFrame(byte[] Nv12, int Width, int Height, long TimeUs);

/// <summary>A chunk of decoded audio, signed 16-bit interleaved.</summary>
public sealed record AudioChunk(short[] Samples, int Channels, int SampleRate, long TimeUs);

/// <summary>
/// Demuxes and decodes one media file (MP4/H.264/AAC and anything else FFmpeg reads) on a background thread into
/// small bounded queues (<see cref="MediaPlayerBase"/>), so the guest's GetVideoData/GetAudioData calls never wait for the decoder.
/// </summary>
public sealed unsafe class MediaPlayer : MediaPlayerBase
{
    AVFormatContext* _fmt;
    AVCodecContext* _video, _audio;
    // H.264 video: Annex B access units into the pluggable decoder (the MP4 avcC header goes in front of key frames)
    IVideoDecoder? _videoDecoder;
    byte[]? _annexBHeader;
    int _nalLengthSize;
    byte[] _annexB = new byte[64 * 1024];
    int _videoStream = -1, _audioStream = -1;
    AVRational _videoTb, _audioTb;

    readonly Thread _thread;

    public override bool HasVideo => _videoStream >= 0;
    public override bool HasAudio => _audioStream >= 0;

    MediaPlayer(AVFormatContext* fmt)
    {
        _fmt = fmt;
        DurationUs = fmt->duration > 0 ? fmt->duration : 0;
        _videoStream = OpenStream(AVMediaType.AVMEDIA_TYPE_VIDEO, out _video, out _videoTb);
        _audioStream = OpenStream(AVMediaType.AVMEDIA_TYPE_AUDIO, out _audio, out _audioTb);
        if (_video != null)
        {
            Width = _video->width;
            Height = _video->height;
            var r = fmt->streams[_videoStream]->avg_frame_rate;
            FrameRate = r.den != 0 && r.num != 0 ? (double)r.num / r.den : 30;
        }
        if (_audio != null)
        {
            Channels = _audio->ch_layout.nb_channels;
            SampleRate = _audio->sample_rate;
        }
        // H.264 goes through the pluggable video decoder (hardware when available); other codecs stay on avcodec
        if (_video != null && _video->codec_id == AVCodecID.AV_CODEC_ID_H264 && VideoDecoders.CreateH264(new VideoDecoderOptions(Width, Height)) is { } dec)
        {
            var par = fmt->streams[_videoStream]->codecpar;
            if (par->extradata_size > 7 && par->extradata[0] == 1)
                (_annexBHeader, _nalLengthSize) = Mp4Demuxer.ParseAvcC(new ReadOnlySpan<byte>(par->extradata, par->extradata_size));
            _videoDecoder = dec;
            var v = _video;
            ffmpeg.avcodec_free_context(&v);
            _video = null;
        }
        _thread = new Thread(DecodeLoop) { IsBackground = true, Name = "AvPlayer decoder" };
        _thread.Start();
    }

    public static MediaPlayer? Open(string path)
    {
        if (!FFmpegLoader.Available || !File.Exists(path)) return null;
        AVFormatContext* fmt = null;
        if (ffmpeg.avformat_open_input(&fmt, path, null, null) < 0) return null;
        if (ffmpeg.avformat_find_stream_info(fmt, null) < 0)
        {
            ffmpeg.avformat_close_input(&fmt);
            return null;
        }
        var p = new MediaPlayer(fmt);
        if (!p.HasVideo && !p.HasAudio)
        {
            p.Dispose();
            return null;
        }
        return p;
    }

    int OpenStream(AVMediaType type, out AVCodecContext* ctx, out AVRational tb)
    {
        ctx = null;
        tb = default;
        AVCodec* codec = null;
        int index = ffmpeg.av_find_best_stream(_fmt, type, -1, -1, &codec, 0);
        if (index < 0 || codec == null) return -1;
        var c = ffmpeg.avcodec_alloc_context3(codec);
        if (ffmpeg.avcodec_parameters_to_context(c, _fmt->streams[index]->codecpar) < 0 || ffmpeg.avcodec_open2(c, codec, null) < 0)
        {
            ffmpeg.avcodec_free_context(&c);
            return -1;
        }
        ctx = c;
        tb = _fmt->streams[index]->time_base;
        return index;
    }

    void DecodeLoop()
    {
        var pkt = ffmpeg.av_packet_alloc();
        var frame = ffmpeg.av_frame_alloc();
        try
        {
            while (!_stop)
            {
                lock (_lock)
                {
                    while (!_stop && _seekRequestUs < 0 && (_demuxDone || (HasVideo ? _videoQueue.Count >= MaxVideoFrames : _audioQueue.Count >= MaxAudioChunks)
                                                           || _audioQueue.Count >= MaxAudioChunks))
                        Monitor.Wait(_lock, 50);
                    if (_stop) break;
                    if (_seekRequestUs >= 0)
                    {
                        ffmpeg.av_seek_frame(_fmt, -1, _seekRequestUs, ffmpeg.AVSEEK_FLAG_BACKWARD);
                        if (_video != null) ffmpeg.avcodec_flush_buffers(_video);
                        _videoDecoder?.Reset();
                        if (_audio != null) ffmpeg.avcodec_flush_buffers(_audio);
                        _seekRequestUs = -1;
                    }
                }

                int r = ffmpeg.av_read_frame(_fmt, pkt);
                if (r < 0)
                {
                    // end of file: drain the decoders, then loop or finish
                    Drain(_video, frame, true);
                    DrainDecoder();
                    Drain(_audio, frame, false);
                    if (Loop)
                    {
                        ffmpeg.av_seek_frame(_fmt, -1, 0, ffmpeg.AVSEEK_FLAG_BACKWARD);
                        if (_video != null) ffmpeg.avcodec_flush_buffers(_video);
                        _videoDecoder?.Reset();
                        if (_audio != null) ffmpeg.avcodec_flush_buffers(_audio);
                        continue;
                    }
                    lock (_lock) _demuxDone = true;
                    continue;
                }
                if (pkt->stream_index == _videoStream) { if (_videoDecoder != null) DecodeH264(pkt); else Decode(_video, pkt, frame, true); }
                else if (pkt->stream_index == _audioStream) Decode(_audio, pkt, frame, false);
                ffmpeg.av_packet_unref(pkt);
            }
        }
        catch (Exception e)
        {
            Log.Error($"AvPlayer decoder: {e.Message}");
            lock (_lock) _demuxDone = true;
        }
        finally
        {
            ffmpeg.av_frame_free(&frame);
            ffmpeg.av_packet_free(&pkt);
        }
    }

    void Decode(AVCodecContext* ctx, AVPacket* pkt, AVFrame* frame, bool video)
    {
        if (ffmpeg.avcodec_send_packet(ctx, pkt) < 0) return;
        while (ffmpeg.avcodec_receive_frame(ctx, frame) == 0)
        {
            if (video) PushVideo(frame); else PushAudio(frame);
            ffmpeg.av_frame_unref(frame);
        }
    }

    void Drain(AVCodecContext* ctx, AVFrame* frame, bool video)
    {
        if (ctx == null) return;
        ffmpeg.avcodec_send_packet(ctx, null);
        while (ffmpeg.avcodec_receive_frame(ctx, frame) == 0)
        {
            if (video) PushVideo(frame); else PushAudio(frame);
            ffmpeg.av_frame_unref(frame);
        }
        ffmpeg.avcodec_flush_buffers(ctx);
    }

    static long ToUs(long ts, AVRational tb) => ts == ffmpeg.AV_NOPTS_VALUE ? 0 : ts * 1_000_000L * tb.num / tb.den;

    /// <summary>One H.264 packet through the pluggable decoder (MP4 packets are converted to Annex B first).</summary>
    void DecodeH264(AVPacket* pkt)
    {
        var data = new ReadOnlySpan<byte>(pkt->data, pkt->size);
        long pts = pkt->pts != ffmpeg.AV_NOPTS_VALUE ? pkt->pts : pkt->dts;
        if (_nalLengthSize > 0)
        {
            bool key = (pkt->flags & ffmpeg.AV_PKT_FLAG_KEY) != 0;
            int n = Mp4Demuxer.ToAnnexB(data, _nalLengthSize, key ? _annexBHeader : null, ref _annexB);
            _videoDecoder!.Send(_annexB.AsSpan(0, n), pts);
        }
        else _videoDecoder!.Send(data, pts);
        PushDecoded();
    }

    void DrainDecoder()
    {
        if (_videoDecoder == null) return;
        _videoDecoder.Flush();
        PushDecoded();
    }

    void PushDecoded()
    {
        while (_videoDecoder!.Take() is { } p)
        {
            PushVideo(p);
            _videoDecoder.Recycle(p);
        }
    }

    void PushVideo(DecodedPicture p) => PushPicture(p, ToUs(p.Pts, _videoTb));

    void PushVideo(AVFrame* f)
    {
        int w = f->width & ~1, h = f->height & ~1;
        var nv12 = RentBuffer(_freeNv12, w * h * 3 / 2);
        var fmt = (AVPixelFormat)f->format;
        fixed (byte* dst = nv12)
        {
            for (int y = 0; y < h; y++) Buffer.MemoryCopy(f->data[0] + y * f->linesize[0], dst + y * w, w, w);
            byte* uv = dst + w * h;
            if (fmt == AVPixelFormat.AV_PIX_FMT_NV12)
            {
                for (int y = 0; y < h / 2; y++)
                {
                    byte* s = f->data[1] + y * f->linesize[1];
                    byte* d = uv + y * w;
                    for (int x = 0; x < w / 2; x++) { d[2 * x] = s[2 * x + 1]; d[2 * x + 1] = s[2 * x]; }
                }
            }
            else if (fmt is AVPixelFormat.AV_PIX_FMT_YUV420P or AVPixelFormat.AV_PIX_FMT_YUVJ420P)
            {
                for (int y = 0; y < h / 2; y++)
                {
                    byte* su = f->data[1] + y * f->linesize[1], sv = f->data[2] + y * f->linesize[2];
                    byte* d = uv + y * w;
                    for (int x = 0; x < w / 2; x++) { d[2 * x] = sv[x]; d[2 * x + 1] = su[x]; }
                }
            }
            else
            {
                Log.WarnOnce("avpix" + fmt, $"AvPlayer: unsupported pixel format {fmt}, showing luma only");
                new Span<byte>(uv, w * h / 2).Fill(128);
            }
        }
        Enqueue(new VideoFrame(nv12, w, h, ToUs(f->best_effort_timestamp, _videoTb)));
    }

    void PushAudio(AVFrame* f)
    {
        int ch = f->ch_layout.nb_channels, n = f->nb_samples;
        if (ch <= 0 || n <= 0) return;
        var outS = RentBuffer(_freeSamples, n * ch);
        var fmt = (AVSampleFormat)f->format;
        bool planar = ffmpeg.av_sample_fmt_is_planar(fmt) != 0;
        for (int c = 0; c < ch; c++)
        {
            byte* plane = planar ? f->extended_data[c] : f->extended_data[0];
            for (int i = 0; i < n; i++)
            {
                int idx = planar ? i : i * ch + c;
                outS[i * ch + c] = fmt switch
                {
                    AVSampleFormat.AV_SAMPLE_FMT_FLTP or AVSampleFormat.AV_SAMPLE_FMT_FLT => (short)Math.Clamp((int)(((float*)plane)[idx] * 32767f), -32768, 32767),
                    AVSampleFormat.AV_SAMPLE_FMT_S16P or AVSampleFormat.AV_SAMPLE_FMT_S16 => ((short*)plane)[idx],
                    AVSampleFormat.AV_SAMPLE_FMT_S32P or AVSampleFormat.AV_SAMPLE_FMT_S32 => (short)(((int*)plane)[idx] >> 16),
                    AVSampleFormat.AV_SAMPLE_FMT_DBLP or AVSampleFormat.AV_SAMPLE_FMT_DBL => (short)Math.Clamp((int)(((double*)plane)[idx] * 32767), -32768, 32767),
                    AVSampleFormat.AV_SAMPLE_FMT_U8P or AVSampleFormat.AV_SAMPLE_FMT_U8 => (short)((plane[idx] - 128) << 8),
                    _ => 0,
                };
            }
        }
        Enqueue(new AudioChunk(outS, ch, f->sample_rate, ToUs(f->best_effort_timestamp, _audioTb)));
    }

    int _disposed;

    /// <summary>
    /// Stops the decoder thread and frees the FFmpeg contexts. Idempotent and safe to race: guest threads close the
    /// same player concurrently (SM2E polls sceAvPlayerIsActive from its game and movie-audio threads; both saw the
    /// movie finished and disposed it), and the second free of the codec contexts corrupted the native heap
    /// (0xC0000374).
    /// </summary>
    public override void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _stop = true;
        lock (_lock) Monitor.PulseAll(_lock);
        if (_thread.IsAlive && Thread.CurrentThread != _thread && !_thread.Join(10_000))
        {
            // freeing the codec contexts under a decoder still using them corrupts the native heap: leak them instead
            Log.Warn("AvPlayer decoder thread did not stop within 10 s; leaking its FFmpeg contexts");
            return;
        }
        _videoDecoder?.Dispose();
        _videoDecoder = null;
        var v = _video; var a = _audio; var fmt = _fmt;
        if (v != null) ffmpeg.avcodec_free_context(&v);
        if (a != null) ffmpeg.avcodec_free_context(&a);
        if (fmt != null) ffmpeg.avformat_close_input(&fmt);
        _video = _audio = null;
        _fmt = null;
    }
}
