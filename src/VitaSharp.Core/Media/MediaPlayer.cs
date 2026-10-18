using FFmpeg.AutoGen;
using VitaSharp.Core.Util;

namespace VitaSharp.Core.Media;

/// <summary>A decoded video frame in YVU420P2 (NV21) layout: Y plane, then interleaved V/U at half resolution.</summary>
public sealed record VideoFrame(byte[] Nv12, int Width, int Height, long TimeUs);

/// <summary>A chunk of decoded audio, signed 16-bit interleaved.</summary>
public sealed record AudioChunk(short[] Samples, int Channels, int SampleRate, long TimeUs);

/// <summary>
/// Demuxes and decodes one media file (MP4/H.264/AAC and anything else FFmpeg reads) on a background thread into
/// small bounded queues, so the guest's GetVideoData/GetAudioData calls never wait for the decoder.
/// </summary>
public sealed unsafe class MediaPlayer : IDisposable
{
    const int MaxVideoFrames = 12;
    const int MaxAudioChunks = 48;

    AVFormatContext* _fmt;
    AVCodecContext* _video, _audio;
    int _videoStream = -1, _audioStream = -1;
    AVRational _videoTb, _audioTb;

    readonly Queue<VideoFrame> _videoQueue = new();
    readonly Queue<AudioChunk> _audioQueue = new();
    readonly object _lock = new();
    readonly Thread _thread;
    volatile bool _stop;
    long _seekRequestUs = -1;
    bool _demuxDone;

    public int Width { get; }
    public int Height { get; }
    public double FrameRate { get; }
    public int Channels { get; }
    public int SampleRate { get; }
    public long DurationUs { get; }
    public bool HasVideo => _videoStream >= 0;
    public bool HasAudio => _audioStream >= 0;
    public bool Loop { get; set; }

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

    // ------------------------------------------------------------------ consumer side

    /// <summary>Next video frame with a timestamp at or before <paramref name="clockUs"/> (dropping late frames).</summary>
    public VideoFrame? TakeVideo(long clockUs)
    {
        lock (_lock)
        {
            VideoFrame? f = null;
            while (_videoQueue.Count > 0 && _videoQueue.Peek().TimeUs <= clockUs) f = _videoQueue.Dequeue();
            if (f != null) Monitor.PulseAll(_lock);
            return f;
        }
    }

    public AudioChunk? TakeAudio()
    {
        lock (_lock)
        {
            if (_audioQueue.Count == 0) return null;
            var c = _audioQueue.Dequeue();
            Monitor.PulseAll(_lock);
            return c;
        }
    }

    /// <summary>Timestamp of the next queued video frame, or -1.</summary>
    public long PeekVideoTime()
    {
        lock (_lock) return _videoQueue.Count > 0 ? _videoQueue.Peek().TimeUs : -1;
    }

    /// <summary>True once the file has been fully decoded and every queued frame consumed.</summary>
    public bool Finished
    {
        get { lock (_lock) return _demuxDone && _videoQueue.Count == 0 && _audioQueue.Count == 0; }
    }

    public void Seek(long timeUs)
    {
        lock (_lock)
        {
            _seekRequestUs = Math.Max(0, timeUs);
            _videoQueue.Clear();
            _audioQueue.Clear();
            _demuxDone = false;
            Monitor.PulseAll(_lock);
        }
    }

    // ------------------------------------------------------------------ decoder thread

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
                        if (_audio != null) ffmpeg.avcodec_flush_buffers(_audio);
                        _seekRequestUs = -1;
                    }
                }

                int r = ffmpeg.av_read_frame(_fmt, pkt);
                if (r < 0)
                {
                    // end of file: drain the decoders, then loop or finish
                    Drain(_video, frame, true);
                    Drain(_audio, frame, false);
                    if (Loop)
                    {
                        ffmpeg.av_seek_frame(_fmt, -1, 0, ffmpeg.AVSEEK_FLAG_BACKWARD);
                        if (_video != null) ffmpeg.avcodec_flush_buffers(_video);
                        if (_audio != null) ffmpeg.avcodec_flush_buffers(_audio);
                        continue;
                    }
                    lock (_lock) _demuxDone = true;
                    continue;
                }
                if (pkt->stream_index == _videoStream) Decode(_video, pkt, frame, true);
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

    void PushVideo(AVFrame* f)
    {
        int w = f->width & ~1, h = f->height & ~1;
        var nv12 = new byte[w * h * 3 / 2];
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
        var vf = new VideoFrame(nv12, w, h, ToUs(f->best_effort_timestamp, _videoTb));
        lock (_lock)
        {
            while (!_stop && _seekRequestUs < 0 && _videoQueue.Count >= MaxVideoFrames) Monitor.Wait(_lock, 50);
            if (_seekRequestUs < 0) _videoQueue.Enqueue(vf);
        }
    }

    void PushAudio(AVFrame* f)
    {
        int ch = f->ch_layout.nb_channels, n = f->nb_samples;
        if (ch <= 0 || n <= 0) return;
        var outS = new short[n * ch];
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
        var chunk = new AudioChunk(outS, ch, f->sample_rate, ToUs(f->best_effort_timestamp, _audioTb));
        lock (_lock)
        {
            while (!_stop && _seekRequestUs < 0 && _audioQueue.Count >= MaxAudioChunks) Monitor.Wait(_lock, 50);
            if (_seekRequestUs < 0) _audioQueue.Enqueue(chunk);
        }
    }

    public void Dispose()
    {
        _stop = true;
        lock (_lock) Monitor.PulseAll(_lock);
        if (_thread.IsAlive && Thread.CurrentThread != _thread) _thread.Join(1000);
        var v = _video; var a = _audio; var fmt = _fmt;
        if (v != null) ffmpeg.avcodec_free_context(&v);
        if (a != null) ffmpeg.avcodec_free_context(&a);
        if (fmt != null) ffmpeg.avformat_close_input(&fmt);
        _video = _audio = null;
        _fmt = null;
    }
}
