using Android.Media;
using FFmpeg.AutoGen;
using VitaSharp.Core.Media;
using VitaSharp.Core.Util;
using JByteBuffer = Java.Nio.ByteBuffer;

namespace VitaSharp.Android.Media;

/// <summary>
/// SceMp4's demuxer on Android (<see cref="Mp4Demuxers.Backend"/>), where the app carries no FFmpeg: android.media
/// .MediaExtractor hands out the access units the FFmpeg <see cref="Mp4Demuxer"/> does on the desktop. H.264 comes
/// out of MediaExtractor as Annex B already (length prefixes are converted when a device's extractor keeps them), with
/// SPS/PPS (csd-0, csd-1) put in front of every key frame; AAC as raw frames. F1 2011 plays its intro movies this way.
/// </summary>
public sealed unsafe class MediaExtractorDemuxer : IMp4Demuxer
{
    readonly MediaExtractor _ex;
    readonly byte[][] _csd;
    readonly bool[] _video;
    JByteBuffer? _sample;
    int _maxSample = 1 << 20;

    public IReadOnlyList<DemuxedStream> Streams { get; }
    public long Duration90k { get; }

    MediaExtractorDemuxer(MediaExtractor ex)
    {
        _ex = ex;
        int n = ex.TrackCount;
        var streams = new List<DemuxedStream>();
        _csd = new byte[n][];
        _video = new bool[n];
        long duration = 0;
        for (int i = 0; i < n; i++)
        {
            var f = ex.GetTrackFormat(i);
            string mime = f.GetString(MediaFormat.KeyMime) ?? "";
            bool video = mime.StartsWith("video/", StringComparison.Ordinal);
            _video[i] = video;
            var codec = mime switch
            {
                "video/avc" => AVCodecID.AV_CODEC_ID_H264,
                "audio/mp4a-latm" => AVCodecID.AV_CODEC_ID_AAC,
                "audio/mpeg" => AVCodecID.AV_CODEC_ID_MP3,
                _ => AVCodecID.AV_CODEC_ID_NONE,
            };
            long dur = f.ContainsKey(MediaFormat.KeyDuration) ? f.GetLong(MediaFormat.KeyDuration) * 9 / 100 : 0;
            duration = Math.Max(duration, dur);
            if (f.ContainsKey(MediaFormat.KeyMaxInputSize)) _maxSample = Math.Max(_maxSample, f.GetInteger(MediaFormat.KeyMaxInputSize));
            streams.Add(new DemuxedStream(video, codec, Int(f, MediaFormat.KeyWidth), Int(f, MediaFormat.KeyHeight), Int(f, MediaFormat.KeyChannelCount),
                Int(f, MediaFormat.KeySampleRate), Int(f, MediaFormat.KeyBitRate), dur, Number(f, MediaFormat.KeyFrameRate),
                f.ContainsKey(MediaFormat.KeyLanguage) ? f.GetString(MediaFormat.KeyLanguage) ?? "und" : "und"));
            if (codec == AVCodecID.AV_CODEC_ID_H264)
            {
                var csd = new List<byte>();
                foreach (var k in new[] { "csd-0", "csd-1" })
                    if (f.ContainsKey(k) && f.GetByteBuffer(k) is { } b) csd.AddRange(Bytes(b));
                _csd[i] = [.. csd];
            }
            ex.SelectTrack(i);
        }
        Streams = streams;
        Duration90k = duration;
    }

    /// <summary>The demuxer for <paramref name="path"/>, or null when MediaExtractor cannot read it.</summary>
    public static IMp4Demuxer? Open(string path)
    {
        if (!File.Exists(path)) return null;
        var ex = new MediaExtractor();
        try
        {
            ex.SetDataSource(path);
            if (ex.TrackCount == 0) { ex.Release(); return null; }
            return new MediaExtractorDemuxer(ex);
        }
        catch (Java.Lang.Exception e)
        {
            Log.Warn($"sceMp4: MediaExtractor cannot read {Path.GetFileName(path)}: {e.Message}");
            ex.Release();
            return null;
        }
    }

    static int Int(MediaFormat f, string key)
    {
        if (!f.ContainsKey(key)) return 0;
        try { return f.GetInteger(key); }
        catch (Java.Lang.ClassCastException) { return (int)f.GetFloat(key); }
    }

    static double Number(MediaFormat f, string key)
    {
        if (!f.ContainsKey(key)) return 0;
        try { return f.GetInteger(key); }
        catch (Java.Lang.ClassCastException) { return f.GetFloat(key); }
    }

    static byte[] Bytes(JByteBuffer b) => AndroidMediaPlayer.Bytes(b);

    public DemuxedUnit? Next(Func<int, bool> enabled)
    {
        lock (_ex)
        {
            for (int track; (track = _ex.SampleTrackIndex) >= 0; _ex.Advance())
            {
                if (!enabled(track)) continue;
                // getSampleSize is API 28; before that the largest sample the tracks declare (max-input-size)
                long need = OperatingSystem.IsAndroidVersionAtLeast(28) ? Math.Max(_ex.SampleSize, 1) : _maxSample;
                if (_sample == null || _sample.Capacity() < need)
                {
                    _sample?.Dispose();
                    _sample = JByteBuffer.AllocateDirect((int)Math.Max(need, _maxSample));
                }
                _sample.Clear();
                int size = _ex.ReadSampleData(_sample, 0);
                long pts = _ex.SampleTime * 9 / 100;
                bool key = (_ex.SampleFlags & MediaExtractorSampleFlags.Sync) != 0;
                var data = size > 0 ? new ReadOnlySpan<byte>((byte*)_sample.GetDirectBufferAddress(), size) : default;
                byte[] bytes = _csd[track] is { } csd ? AnnexB(data, key ? csd : null) : data.ToArray();
                _ex.Advance();
                return new DemuxedUnit(track, bytes, pts, pts, key);
            }
            return null;
        }
    }

    /// <summary>An H.264 sample as Annex B, with SPS/PPS in front of key frames. MediaExtractor's MP4 extractor already
    /// writes start codes; 4-byte length prefixes (another extractor) are rewritten.</summary>
    static byte[] AnnexB(ReadOnlySpan<byte> s, byte[]? header)
    {
        bool startCodes = s.Length >= 4 && s[0] == 0 && s[1] == 0 && (s[2] == 1 || (s[2] == 0 && s[3] == 1));
        var ms = new MemoryStream(s.Length + (header?.Length ?? 0) + 16);
        if (header != null) ms.Write(header);
        if (startCodes) { ms.Write(s); return ms.ToArray(); }
        for (int p = 0; p + 4 <= s.Length;)
        {
            int l = (s[p] << 24) | (s[p + 1] << 16) | (s[p + 2] << 8) | s[p + 3];
            p += 4;
            if (l <= 0 || p + l > s.Length) break;
            ms.Write([0, 0, 0, 1]);
            ms.Write(s.Slice(p, l));
            p += l;
        }
        return ms.ToArray();
    }

    public void Seek(long pts90k)
    {
        lock (_ex) _ex.SeekTo(pts90k * 100 / 9, MediaExtractorSeekTo.PreviousSync);
    }

    public void Dispose()
    {
        lock (_ex)
        {
            _sample?.Dispose();
            _sample = null;
            _ex.Release();
        }
    }
}
