using Android.Media;
using FFmpeg.AutoGen;
using VitaSharp.Core.Media;
using VitaSharp.Core.Util;
using JByteBuffer = Java.Nio.ByteBuffer;

namespace VitaSharp.Android.Media;

/// <summary>
/// Built into the app, not run on a device yet. SceMp4's demuxer on Android, where the app carries no FFmpeg:
/// android.media.MediaExtractor reads the MP4 (the guest file, or the temp copy SceMp4 makes through the game's file
/// callbacks) into the units FFmpeg's <see cref="Mp4Demuxer"/> gives on the desktop: H.264 as Annex B with SPS/PPS
/// (csd-0/csd-1) in front of every key frame, AAC as raw frames, 90 kHz timestamps. MediaExtractor reports no decode
/// timestamps: DTS = PTS. Installed by EmulationSession as <c>Mp4Demuxers.Backend</c>.
/// </summary>
public sealed unsafe class MediaExtractorMp4Demuxer : IMp4Demuxer
{
    MediaExtractor? _ex;
    readonly byte[][] _csd;
    readonly bool[] _h264;
    JByteBuffer? _sample;
    byte[] _au = new byte[64 * 1024];
    int _maxSample = 1 << 20;

    public IReadOnlyList<DemuxedStream> Streams { get; }
    public long Duration90k { get; }

    MediaExtractorMp4Demuxer(MediaExtractor ex)
    {
        _ex = ex;
        int n = ex.TrackCount;
        _csd = new byte[n][];
        _h264 = new bool[n];
        var streams = new List<DemuxedStream>();
        for (int i = 0; i < n; i++)
        {
            var f = ex.GetTrackFormat(i);
            string mime = f.GetString(MediaFormat.KeyMime) ?? "";
            bool video = mime.StartsWith("video/", StringComparison.Ordinal);
            var codec = mime switch
            {
                "video/avc" => AVCodecID.AV_CODEC_ID_H264,
                "audio/mp4a-latm" => AVCodecID.AV_CODEC_ID_AAC,
                "audio/mpeg" => AVCodecID.AV_CODEC_ID_MP3,
                _ => AVCodecID.AV_CODEC_ID_NONE,
            };
            long dur = f.ContainsKey(MediaFormat.KeyDuration) ? Mp4Demuxers.UsTo90k(f.GetLong(MediaFormat.KeyDuration)) : 0;
            if (f.ContainsKey(MediaFormat.KeyMaxInputSize)) _maxSample = Math.Max(_maxSample, f.GetInteger(MediaFormat.KeyMaxInputSize));
            int Int(string key) => f.ContainsKey(key) ? f.GetInteger(key) : 0;
            double fps = 0;
            if (video && f.ContainsKey(MediaFormat.KeyFrameRate))
            {
                try { fps = f.GetInteger(MediaFormat.KeyFrameRate); }
                catch (Java.Lang.ClassCastException) { fps = f.GetFloat(MediaFormat.KeyFrameRate); }
            }
            if (video && fps <= 0) fps = 30; // not every extractor reports it; FFmpeg's avg_frame_rate has it
            string lang = f.ContainsKey(MediaFormat.KeyLanguage) ? f.GetString(MediaFormat.KeyLanguage) ?? "und" : "und";
            streams.Add(new DemuxedStream(video, codec, Int(MediaFormat.KeyWidth), Int(MediaFormat.KeyHeight),
                video ? 0 : Int(MediaFormat.KeyChannelCount), video ? 0 : Int(MediaFormat.KeySampleRate), Int(MediaFormat.KeyBitRate),
                dur, fps, lang));
            if (codec == AVCodecID.AV_CODEC_ID_H264)
            {
                _h264[i] = true;
                var csd = new List<byte>();
                foreach (var k in new[] { "csd-0", "csd-1" })
                    if (f.GetByteBuffer(k) is { } b) csd.AddRange(Bytes(b));
                _csd[i] = [.. csd];
            }
            ex.SelectTrack(i);
        }
        Streams = streams;
        Duration90k = streams.Count > 0 ? streams.Max(s => s.Duration90k) : 0;
    }

    static byte[] Bytes(JByteBuffer b)
    {
        var a = new byte[b.Remaining()];
        new ReadOnlySpan<byte>((byte*)b.GetDirectBufferAddress() + b.Position(), a.Length).CopyTo(a);
        return a;
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
            var d = new MediaExtractorMp4Demuxer(ex);
            Log.Info($"Mp4 (MediaExtractor) {Path.GetFileName(path)}: " + string.Join(", ", d.Streams.Select(s =>
                s.Video ? $"video {s.Codec} {s.Width}x{s.Height} {s.FrameRate:F2} fps" : $"audio {s.Codec} {s.Channels} ch {s.SampleRate} Hz")));
            return d;
        }
        catch (Java.Lang.Exception e)
        {
            Log.Warn($"Mp4: MediaExtractor cannot read {Path.GetFileName(path)}: {e.Message}");
            ex.Release();
            return null;
        }
    }

    public DemuxedUnit? Next(Func<int, bool> enabled)
    {
        var ex = _ex;
        if (ex == null) return null;
        try
        {
            for (int track; (track = ex.SampleTrackIndex) >= 0; ex.Advance())
            {
                if (!enabled(track)) continue;
                // getSampleSize is API 28; before that the largest sample the tracks declare (max-input-size)
                long need = OperatingSystem.IsAndroidVersionAtLeast(28) ? Math.Max(ex.SampleSize, 1) : _maxSample;
                if (_sample == null || _sample.Capacity() < need)
                {
                    _sample?.Dispose();
                    _sample = JByteBuffer.AllocateDirect((int)Math.Max(need, _maxSample));
                }
                _sample.Clear();
                int size = ex.ReadSampleData(_sample, 0);
                if (size < 0) continue;
                long pts = Mp4Demuxers.UsTo90k(Math.Max(0, ex.SampleTime));
                bool key = (ex.SampleFlags & MediaExtractorSampleFlags.Sync) != 0;
                var data = new ReadOnlySpan<byte>((byte*)_sample.GetDirectBufferAddress(), size);
                byte[] bytes;
                if (_h264[track])
                {
                    int n = H264AnnexB.BuildAccessUnit(data, key ? _csd[track] : [], ref _au);
                    bytes = _au.AsSpan(0, n).ToArray();
                }
                else bytes = data.ToArray();
                ex.Advance();
                return new DemuxedUnit(track, bytes, pts, pts, key);
            }
        }
        catch (Java.Lang.Exception e) { Log.WarnOnce("mp4-extractor", $"Mp4: MediaExtractor failed: {e.Message}"); }
        return null;
    }

    public void Seek(long pts90k)
    {
        try { _ex?.SeekTo(Mp4Demuxers.Ticks90kToUs(pts90k), MediaExtractorSeekTo.PreviousSync); }
        catch (Java.Lang.Exception e) { Log.WarnOnce("mp4-extractor-seek", $"Mp4: seek failed: {e.Message}"); }
    }

    public void Dispose()
    {
        _ex?.Release();
        _ex = null;
        _sample?.Dispose();
        _sample = null;
    }
}
