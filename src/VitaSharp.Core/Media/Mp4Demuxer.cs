using FFmpeg.AutoGen;

namespace VitaSharp.Core.Media;

/// <summary>One demuxed access unit.</summary>
public sealed record DemuxedUnit(int Stream, byte[] Data, long Pts90k, long Dts90k, bool Key);

/// <summary>Stream description for SceMp4.</summary>
public sealed record DemuxedStream(bool Video, AVCodecID Codec, int Width, int Height, int Channels, int SampleRate,
    int BitRate, long Duration90k, double FrameRate, string Language);

/// <summary>An MP4 demuxer for SceMp4: access units of every stream in file order (H.264 as Annex B with SPS/PPS
/// before every key frame, AAC as raw frames), timestamps in 90 kHz units.</summary>
public interface IMp4Demuxer : IDisposable
{
    IReadOnlyList<DemuxedStream> Streams { get; }
    long Duration90k { get; }
    /// <summary>Next unit of any enabled stream in file order, or null at the end.</summary>
    DemuxedUnit? Next(Func<int, bool> enabled);
    void Seek(long pts90k);
}

/// <summary>Opens MP4 files for SceMp4: FFmpeg (<see cref="Mp4Demuxer"/>) unless a front-end installs another
/// <see cref="Backend"/> (Android: MediaExtractor, there being no FFmpeg in the app).</summary>
public static class Mp4Demuxers
{
    public static Func<string, IMp4Demuxer?>? Backend { get; set; }

    public static IMp4Demuxer? Open(string path) => Backend?.Invoke(path) ?? Mp4Demuxer.Open(path);
}

/// <summary>Demuxes an MP4/MOV container with FFmpeg into access units (no decoding). H.264 is returned as an
/// Annex B byte stream with SPS/PPS before every key frame, the format hardware decoders take.</summary>
public sealed unsafe class Mp4Demuxer : IMp4Demuxer
{
    AVFormatContext* _fmt;
    readonly AVPacket* _pkt;
    readonly byte[][] _annexBHeaders;
    readonly int[] _nalLengthSize;

    public IReadOnlyList<DemuxedStream> Streams { get; }
    public long Duration90k { get; }

    Mp4Demuxer(AVFormatContext* fmt)
    {
        _fmt = fmt;
        _pkt = ffmpeg.av_packet_alloc();
        int n = (int)fmt->nb_streams;
        var streams = new List<DemuxedStream>();
        _annexBHeaders = new byte[n][];
        _nalLengthSize = new int[n];
        for (int i = 0; i < n; i++)
        {
            var st = fmt->streams[i];
            var par = st->codecpar;
            long dur = st->duration > 0 ? To90k(st->duration, st->time_base) : fmt->duration > 0 ? fmt->duration * 9 / 100 : 0;
            var lang = ffmpeg.av_dict_get(st->metadata, "language", null, 0);
            string language = lang != null ? new string((sbyte*)lang->value) : "und";
            var fr = st->avg_frame_rate;
            streams.Add(new DemuxedStream(par->codec_type == AVMediaType.AVMEDIA_TYPE_VIDEO, par->codec_id, par->width, par->height,
                par->ch_layout.nb_channels, par->sample_rate, (int)par->bit_rate, dur, fr.den != 0 ? (double)fr.num / fr.den : 0, language));
            if (par->codec_id == AVCodecID.AV_CODEC_ID_H264 && par->extradata_size > 7 && par->extradata[0] == 1)
                (_annexBHeaders[i], _nalLengthSize[i]) = ParseAvcC(new ReadOnlySpan<byte>(par->extradata, par->extradata_size));
        }
        Streams = streams;
        Duration90k = fmt->duration > 0 ? fmt->duration * 9 / 100 : streams.Count > 0 ? streams.Max(s => s.Duration90k) : 0;
    }

    public static Mp4Demuxer? Open(string path)
    {
        if (!FFmpegLoader.Available || !File.Exists(path)) return null;
        AVFormatContext* fmt = null;
        if (ffmpeg.avformat_open_input(&fmt, path, null, null) < 0) return null;
        if (ffmpeg.avformat_find_stream_info(fmt, null) < 0) { ffmpeg.avformat_close_input(&fmt); return null; }
        return new Mp4Demuxer(fmt);
    }

    static long To90k(long ts, AVRational tb) => ts == ffmpeg.AV_NOPTS_VALUE ? 0 : ts * 90000L * tb.num / tb.den;

    /// <summary>avcC: SPS/PPS as Annex B, plus the NAL length field size.</summary>
    internal static (byte[], int) ParseAvcC(ReadOnlySpan<byte> c)
    {
        int lenSize = (c[4] & 3) + 1;
        var ms = new MemoryStream();
        int p = 5, sps = c[p++] & 0x1F;
        for (int i = 0; i < sps && p + 2 <= c.Length; i++)
        {
            int l = (c[p] << 8) | c[p + 1]; p += 2;
            ms.Write([0, 0, 0, 1]); ms.Write(c.Slice(p, Math.Min(l, c.Length - p))); p += l;
        }
        if (p < c.Length)
        {
            int pps = c[p++];
            for (int i = 0; i < pps && p + 2 <= c.Length; i++)
            {
                int l = (c[p] << 8) | c[p + 1]; p += 2;
                ms.Write([0, 0, 0, 1]); ms.Write(c.Slice(p, Math.Min(l, c.Length - p))); p += l;
            }
        }
        return (ms.ToArray(), lenSize);
    }

    /// <summary>Next unit of any stream in file order, or null at the end.</summary>
    public DemuxedUnit? Next(Func<int, bool> enabled)
    {
        while (ffmpeg.av_read_frame(_fmt, _pkt) >= 0)
        {
            try
            {
                int s = _pkt->stream_index;
                if (!enabled(s)) continue;
                var tb = _fmt->streams[s]->time_base;
                var data = new ReadOnlySpan<byte>(_pkt->data, _pkt->size);
                bool key = (_pkt->flags & ffmpeg.AV_PKT_FLAG_KEY) != 0;
                byte[] bytes = _annexBHeaders[s] != null ? ToAnnexB(data, _nalLengthSize[s], key ? _annexBHeaders[s] : null) : data.ToArray();
                long pts = To90k(_pkt->pts, tb), dts = _pkt->dts == ffmpeg.AV_NOPTS_VALUE ? pts : To90k(_pkt->dts, tb);
                return new DemuxedUnit(s, bytes, pts, dts, key);
            }
            finally { ffmpeg.av_packet_unref(_pkt); }
        }
        return null;
    }

    /// <summary>
    /// Converts a length-prefixed (MP4) access unit to Annex B in <paramref name="buffer"/> (grown as needed and reused
    /// across calls), with <paramref name="header"/> (SPS/PPS) in front when given. Returns the length written.
    /// </summary>
    internal static int ToAnnexB(ReadOnlySpan<byte> avcc, int lenSize, byte[]? header, ref byte[] buffer)
    {
        int total = header?.Length ?? 0;
        for (int p = 0; Nal(avcc, lenSize, ref p, out _, out int l);) total += 4 + l;
        if (buffer.Length < total) buffer = new byte[Math.Max(total, buffer.Length * 2)];
        int o = 0;
        if (header != null) { header.CopyTo(buffer, 0); o = header.Length; }
        for (int p = 0; Nal(avcc, lenSize, ref p, out int start, out int l);)
        {
            buffer[o] = 0; buffer[o + 1] = 0; buffer[o + 2] = 0; buffer[o + 3] = 1;
            avcc.Slice(start, l).CopyTo(buffer.AsSpan(o + 4));
            o += 4 + l;
        }
        return total;
    }

    static byte[] ToAnnexB(ReadOnlySpan<byte> avcc, int lenSize, byte[]? header)
    {
        // two passes (size, then copy): one exact-size array instead of a growing stream plus a copy
        int total = header?.Length ?? 0;
        for (int p = 0; Nal(avcc, lenSize, ref p, out _, out int l);) total += 4 + l;
        var result = new byte[total];
        int o = 0;
        if (header != null) { header.CopyTo(result, 0); o = header.Length; }
        for (int p = 0; Nal(avcc, lenSize, ref p, out int start, out int l);)
        {
            result[o + 3] = 1; // start code 00 00 00 01
            avcc.Slice(start, l).CopyTo(result.AsSpan(o + 4));
            o += 4 + l;
        }
        return result;
    }

    /// <summary>Next length-prefixed NAL unit at <paramref name="p"/> (advanced past it); false at the end or on a bad length.</summary>
    static bool Nal(ReadOnlySpan<byte> avcc, int lenSize, ref int p, out int start, out int length)
    {
        start = length = 0;
        if (p + lenSize > avcc.Length) return false;
        int l = 0;
        for (int i = 0; i < lenSize; i++) l = (l << 8) | avcc[p + i];
        p += lenSize;
        if (l <= 0 || p + l > avcc.Length) return false;
        start = p;
        length = l;
        p += l;
        return true;
    }

    public void Seek(long pts90k)
    {
        ffmpeg.av_seek_frame(_fmt, -1, pts90k * 100 / 9, ffmpeg.AVSEEK_FLAG_BACKWARD);
    }

    public void Dispose()
    {
        var p = _pkt; ffmpeg.av_packet_free(&p);
        var f = _fmt;
        if (f != null) ffmpeg.avformat_close_input(&f);
        _fmt = null;
    }
}
