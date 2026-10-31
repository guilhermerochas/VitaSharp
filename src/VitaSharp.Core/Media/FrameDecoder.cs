using FFmpeg.AutoGen;

namespace VitaSharp.Core.Media;

/// <summary>Decodes individual compressed audio frames (MP3, AAC) with FFmpeg into interleaved S16.</summary>
public sealed unsafe class FrameDecoder : IAudioFrameDecoder
{
    AVCodecContext* _ctx;
    readonly AVPacket* _pkt;
    readonly AVFrame* _frame;
    readonly AVCodecID _id;
    readonly int _rate, _channels;
    readonly byte[]? _extradata;
    /// <summary>Second decoder of the same stream that only measures raw frame lengths (see <see cref="RawFrameLength"/>).</summary>
    AVCodecContext* _probe;
    AVFrame* _probeFrame;
    AVPacket* _probePkt;

    public int Channels => _ctx->ch_layout.nb_channels;
    public int SampleRate => _ctx->sample_rate;

    FrameDecoder(AVCodecContext* ctx, AVCodecID id, int rate, int channels, byte[]? extradata)
    {
        _ctx = ctx;
        _id = id; _rate = rate; _channels = channels; _extradata = extradata;
        _pkt = ffmpeg.av_packet_alloc();
        _frame = ffmpeg.av_frame_alloc();
    }

    /// <summary>Creates a decoder; <paramref name="extradata"/> is the AAC AudioSpecificConfig for raw (non-ADTS) streams.</summary>
    public static FrameDecoder? Create(AVCodecID id, int sampleRate, int channels, byte[]? extradata = null)
    {
        if (!FFmpegLoader.Available) return null;
        var ctx = Open(id, sampleRate, channels, extradata);
        return ctx == null ? null : new FrameDecoder(ctx, id, sampleRate, channels, extradata);
    }

    static AVCodecContext* Open(AVCodecID id, int sampleRate, int channels, byte[]? extradata)
    {
        var codec = ffmpeg.avcodec_find_decoder(id);
        if (codec == null) return null;
        var ctx = ffmpeg.avcodec_alloc_context3(codec);
        if (sampleRate > 0) ctx->sample_rate = sampleRate;
        if (channels > 0) ffmpeg.av_channel_layout_default(&ctx->ch_layout, channels);
        // A hardware frame decoder turns every compressed frame into a full frame of PCM; trimming encoder delay is
        // the caller's business. FFmpeg would otherwise trim it by itself (e.g. 1024 samples when the first AAC frame
        // carries a "libfaac x.y" fill element) and return 0 samples for that frame, which
        // games read as "0 bytes / 0 Hz" (Persona 4 Golden's movie player derives its output rate from it).
        ctx->flags2 |= ffmpeg.AV_CODEC_FLAG2_SKIP_MANUAL;
        if (extradata != null)
        {
            ctx->extradata = (byte*)ffmpeg.av_mallocz((ulong)extradata.Length + ffmpeg.AV_INPUT_BUFFER_PADDING_SIZE);
            fixed (byte* e = extradata) Buffer.MemoryCopy(e, ctx->extradata, extradata.Length, extradata.Length);
            ctx->extradata_size = extradata.Length;
        }
        if (ffmpeg.avcodec_open2(ctx, codec, null) < 0)
        {
            ffmpeg.avcodec_free_context(&ctx);
            return null;
        }
        return ctx;
    }

    /// <summary>
    /// Byte length of the raw (unframed, e.g. MP4-style AAC) frame at <paramref name="data"/>: hardware decoders parse
    /// exactly one frame from the buffer and report how many bytes it used (SceAudiodecCtrl.inputEsSize). FFmpeg's
    /// send/receive API has no such count, and it decodes whatever follows the frame in the packet as further frames,
    /// which for a game's fixed-size ES slots means the next slot's data or stale bytes: errors, wrong overlap state,
    /// garbage PCM. The length is found on a separate probe decoder as the shortest prefix that decodes one frame
    /// (a truncated raw_data_block fails before its END element); the probe never touches the real decoder's state.
    /// Returns <paramref name="maxSize"/> when no shorter prefix decodes.
    /// </summary>
    public int RawFrameLength(byte* data, int maxSize)
    {
        if (maxSize <= 1) return maxSize;
        if (_probe == null)
        {
            _probe = Open(_id, _rate, _channels, _extradata);
            if (_probe == null) return maxSize;
            _probe->log_level_offset = 32; // the failing trials are expected: keep them out of the error log
            _probeFrame = ffmpeg.av_frame_alloc();
            _probePkt = ffmpeg.av_packet_alloc();
        }
        if (!ProbeDecodes(data, maxSize)) return maxSize;
        int lo = 0, hi = maxSize; // lo fails, hi decodes
        while (hi - lo > 1)
        {
            int mid = (lo + hi) / 2;
            if (ProbeDecodes(data, mid)) hi = mid; else lo = mid;
        }
        return hi;
    }

    bool ProbeDecodes(byte* data, int size)
    {
        ffmpeg.avcodec_flush_buffers(_probe);
        var buf = (byte*)ffmpeg.av_malloc((ulong)size + ffmpeg.AV_INPUT_BUFFER_PADDING_SIZE);
        Buffer.MemoryCopy(data, buf, size, size);
        new Span<byte>(buf + size, ffmpeg.AV_INPUT_BUFFER_PADDING_SIZE).Clear();
        if (ffmpeg.av_packet_from_data(_probePkt, buf, size) < 0)
        {
            ffmpeg.av_free(buf);
            return false;
        }
        bool ok = ffmpeg.avcodec_send_packet(_probe, _probePkt) >= 0 && ffmpeg.avcodec_receive_frame(_probe, _probeFrame) == 0;
        ffmpeg.av_frame_unref(_probeFrame);
        ffmpeg.av_packet_unref(_probePkt);
        return ok;
    }

    /// <summary>AudioSpecificConfig for AAC-LC with the given rate and channel count.</summary>
    public static byte[] AacConfig(int sampleRate, int channels)
    {
        int[] rates = [96000, 88200, 64000, 48000, 44100, 32000, 24000, 22050, 16000, 12000, 11025, 8000, 7350];
        int idx = Array.IndexOf(rates, sampleRate);
        if (idx < 0) idx = 3;
        int v = (2 << 11) | (idx << 7) | (Math.Clamp(channels, 1, 7) << 3); // object type 2 (LC)
        return [(byte)(v >> 8), (byte)v];
    }

    /// <summary>Decodes exactly one frame of <paramref name="size"/> bytes; returns samples written per channel (0 on error).</summary>
    /// <param name="outChannels">channel layout of <paramref name="pcmOut"/> (0 = the stream's): mono is duplicated, extra channels dropped</param>
    public int Decode(byte* data, int size, Span<short> pcmOut, int outChannels = 0)
    {
        var buf = (byte*)ffmpeg.av_malloc((ulong)size + ffmpeg.AV_INPUT_BUFFER_PADDING_SIZE);
        Buffer.MemoryCopy(data, buf, size, size);
        new Span<byte>(buf + size, ffmpeg.AV_INPUT_BUFFER_PADDING_SIZE).Clear();
        if (ffmpeg.av_packet_from_data(_pkt, buf, size) < 0)
        {
            ffmpeg.av_free(buf);
            return 0;
        }
        int total = 0;
        if (ffmpeg.avcodec_send_packet(_ctx, _pkt) >= 0)
        {
            while (ffmpeg.avcodec_receive_frame(_ctx, _frame) == 0)
            {
                int oc = outChannels > 0 ? outChannels : _frame->ch_layout.nb_channels;
                if (total * oc < pcmOut.Length) total += Convert(_frame, pcmOut[(total * oc)..], oc);
                ffmpeg.av_frame_unref(_frame);
            }
        }
        ffmpeg.av_packet_unref(_pkt);
        return total;
    }

    static int Convert(AVFrame* f, Span<short> dst, int outCh)
    {
        int ch = Math.Max(f->ch_layout.nb_channels, 1), n = Math.Min(f->nb_samples, dst.Length / Math.Max(outCh, 1));
        var fmt = (AVSampleFormat)f->format;
        bool planar = ffmpeg.av_sample_fmt_is_planar(fmt) != 0;
        for (int c = 0; c < outCh; c++)
        {
            int sc = Math.Min(c, ch - 1); // source channel: mono is duplicated, extra channels dropped
            byte* plane = planar ? f->extended_data[sc] : f->extended_data[0];
            for (int i = 0; i < n; i++)
            {
                int idx = planar ? i : i * ch + sc;
                dst[i * outCh + c] = fmt switch
                {
                    AVSampleFormat.AV_SAMPLE_FMT_FLTP or AVSampleFormat.AV_SAMPLE_FMT_FLT => (short)Math.Clamp((int)(((float*)plane)[idx] * 32767f), -32768, 32767),
                    AVSampleFormat.AV_SAMPLE_FMT_S16P or AVSampleFormat.AV_SAMPLE_FMT_S16 => ((short*)plane)[idx],
                    AVSampleFormat.AV_SAMPLE_FMT_S32P or AVSampleFormat.AV_SAMPLE_FMT_S32 => (short)(((int*)plane)[idx] >> 16),
                    _ => 0,
                };
            }
        }
        return n;
    }

    public void Flush() => ffmpeg.avcodec_flush_buffers(_ctx);

    public void Dispose()
    {
        var ctx = _ctx;
        if (ctx != null) ffmpeg.avcodec_free_context(&ctx);
        _ctx = null;
        var p = _pkt; ffmpeg.av_packet_free(&p);
        var f = _frame; ffmpeg.av_frame_free(&f);
        if (_probe != null)
        {
            var pc = _probe; ffmpeg.avcodec_free_context(&pc);
            _probe = null;
            var pf = _probeFrame; ffmpeg.av_frame_free(&pf);
            var pp = _probePkt; ffmpeg.av_packet_free(&pp);
        }
    }

    // ------------------------------------------------------------------ frame sizes from headers

    static readonly int[] Mp3BitrateV1 = [0, 32, 40, 48, 56, 64, 80, 96, 112, 128, 160, 192, 224, 256, 320, 0];
    static readonly int[] Mp3BitrateV2 = [0, 8, 16, 24, 32, 40, 48, 56, 64, 80, 96, 112, 128, 144, 160, 0];
    static readonly int[] Mp3RateV1 = [44100, 48000, 32000, 0];

    /// <summary>Length in bytes of the MPEG audio layer III frame starting at <paramref name="p"/>, or 0.</summary>
    public static int Mp3FrameLength(ReadOnlySpan<byte> p)
    {
        if (p.Length < 4 || p[0] != 0xFF || (p[1] & 0xE0) != 0xE0) return 0;
        int version = (p[1] >> 3) & 3; // 3 = MPEG1, 2 = MPEG2, 0 = MPEG2.5
        int layer = (p[1] >> 1) & 3;   // 1 = layer III
        int brIdx = p[2] >> 4, srIdx = (p[2] >> 2) & 3, pad = (p[2] >> 1) & 1;
        if (version == 1 || layer != 1 || srIdx == 3) return 0;
        int rate = Mp3RateV1[srIdx] >> (version == 3 ? 0 : version == 2 ? 1 : 2);
        int bitrate = (version == 3 ? Mp3BitrateV1 : Mp3BitrateV2)[brIdx] * 1000;
        if (bitrate == 0 || rate == 0) return 0;
        return (version == 3 ? 144 : 72) * bitrate / rate + pad;
    }

    /// <summary>Length of the ADTS frame (header included) at <paramref name="p"/>, or 0 when it is not ADTS.</summary>
    public static int AdtsFrameLength(ReadOnlySpan<byte> p)
    {
        if (p.Length < 7 || p[0] != 0xFF || (p[1] & 0xF6) != 0xF0) return 0;
        return ((p[3] & 3) << 11) | (p[4] << 3) | (p[5] >> 5);
    }
}
