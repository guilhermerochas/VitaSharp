using Android.Media;
using VitaSharp.Core.Media;
using VitaSharp.Core.Util;
using JByteBuffer = Java.Nio.ByteBuffer;

namespace VitaSharp.Android.Media;

/// <summary>
/// Built into the app, not run on a device yet. SceAvPlayer's media player on Android, where the app carries no FFmpeg:
/// android.media.MediaExtractor demuxes the MP4, H.264 goes through the pluggable <see cref="IVideoDecoder"/>
/// (<see cref="MediaCodecVideoBackend"/>, the same path SceAvcdec uses) and AAC through a MediaCodec audio decoder,
/// into the queues of <see cref="MediaPlayerBase"/> (NV21 frames, 16-bit audio chunks), exactly what the FFmpeg player
/// hands the guest on the desktop. Installed by EmulationSession with <c>MediaPlayers.Backend = AndroidMediaPlayer.Open</c>.
/// </summary>
public sealed unsafe class AndroidMediaPlayer : MediaPlayerBase
{
    MediaExtractor? _extractor;
    readonly int _videoTrack = -1, _audioTrack = -1;
    IVideoDecoder? _video;
    MediaCodec? _audio;
    readonly MediaCodec.BufferInfo _audioInfo = new();
    byte[] _csd = [];          // SPS/PPS (csd-0 + csd-1, Annex B) put in front of every key frame
    byte[] _au = new byte[64 * 1024];
    JByteBuffer? _sample;
    int _audioChannels, _audioRate;
    int _maxSample = 1 << 20;
    readonly Thread _thread;
    int _disposed;

    public override bool HasVideo => _videoTrack >= 0;
    public override bool HasAudio => _audioTrack >= 0;

    AndroidMediaPlayer(MediaExtractor ex, int videoTrack, int audioTrack)
    {
        _extractor = ex;
        long duration = 0;
        if (videoTrack >= 0)
        {
            var f = ex.GetTrackFormat(videoTrack);
            if (f.ContainsKey(MediaFormat.KeyMaxInputSize)) _maxSample = Math.Max(_maxSample, f.GetInteger(MediaFormat.KeyMaxInputSize));
            Width = f.GetInteger(MediaFormat.KeyWidth);
            Height = f.GetInteger(MediaFormat.KeyHeight);
            FrameRate = f.ContainsKey(MediaFormat.KeyFrameRate) ? Number(f, MediaFormat.KeyFrameRate) : 30;
            if (FrameRate <= 0) FrameRate = 30;
            if (f.ContainsKey(MediaFormat.KeyDuration)) duration = Math.Max(duration, f.GetLong(MediaFormat.KeyDuration));
            var csd = new List<byte>();
            foreach (var k in new[] { "csd-0", "csd-1" })
                if (f.GetByteBuffer(k) is { } b) csd.AddRange(Bytes(b));
            _csd = [.. csd];
            _video = VideoDecoders.CreateH264(new VideoDecoderOptions(Width, Height));
            if (_video == null) Log.Warn("AvPlayer: no H.264 decoder: the movie plays without video");
            else ex.SelectTrack(videoTrack);
        }
        if (audioTrack >= 0)
        {
            var f = ex.GetTrackFormat(audioTrack);
            Channels = _audioChannels = f.GetInteger(MediaFormat.KeyChannelCount);
            SampleRate = _audioRate = f.GetInteger(MediaFormat.KeySampleRate);
            if (f.ContainsKey(MediaFormat.KeyDuration)) duration = Math.Max(duration, f.GetLong(MediaFormat.KeyDuration));
            try
            {
                _audio = MediaCodec.CreateDecoderByType(f.GetString(MediaFormat.KeyMime)!);
                _audio.Configure(f, null, null, MediaCodecConfigFlags.None);
                _audio.Start();
                ex.SelectTrack(audioTrack);
            }
            catch (Java.Lang.Exception e)
            {
                Log.Warn($"AvPlayer: no decoder for {f.GetString(MediaFormat.KeyMime)}: the movie plays without sound ({e.Message})");
                try { _audio?.Release(); } catch (Java.Lang.Exception) { }
                _audio = null;
            }
        }
        _videoTrack = _video != null ? videoTrack : -1;
        _audioTrack = _audio != null ? audioTrack : -1;
        DurationUs = duration;
        _thread = new Thread(DecodeLoop) { IsBackground = true, Name = "AvPlayer decoder" };
        _thread.Start();
    }

    static double Number(MediaFormat f, string key)
    {
        try { return f.GetInteger(key); }
        catch (Java.Lang.ClassCastException) { return f.GetFloat(key); }
    }

    static byte[] Bytes(JByteBuffer b)
    {
        var a = new byte[b.Remaining()];
        new ReadOnlySpan<byte>((byte*)b.GetDirectBufferAddress() + b.Position(), a.Length).CopyTo(a);
        return a;
    }

    /// <summary>The player for <paramref name="path"/>, or null when MediaExtractor cannot read it.</summary>
    public static MediaPlayerBase? Open(string path)
    {
        if (!File.Exists(path)) return null;
        var ex = new MediaExtractor();
        try
        {
            ex.SetDataSource(path);
            int video = -1, audio = -1;
            for (int i = 0; i < ex.TrackCount; i++)
            {
                string mime = ex.GetTrackFormat(i).GetString(MediaFormat.KeyMime) ?? "";
                if (video < 0 && mime == "video/avc") video = i;
                else if (audio < 0 && mime.StartsWith("audio/", StringComparison.Ordinal)) audio = i;
                else if (mime.StartsWith("video/", StringComparison.Ordinal)) Log.WarnOnce("avplayer-mime" + mime, $"AvPlayer: video codec {mime} is not supported (H.264 only)");
            }
            var p = new AndroidMediaPlayer(ex, video, audio);
            if (!p.HasVideo && !p.HasAudio) { p.Dispose(); return null; }
            return p;
        }
        catch (Java.Lang.Exception e)
        {
            Log.Warn($"AvPlayer: MediaExtractor cannot read {Path.GetFileName(path)}: {e.Message}");
            ex.Release();
            return null;
        }
    }

    void DecodeLoop()
    {
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
                        Rewind(_seekRequestUs);
                        _seekRequestUs = -1;
                    }
                }
                var ex = _extractor!;
                int track = ex.SampleTrackIndex;
                if (track < 0)
                {
                    // end of file: drain the decoders, then loop or finish
                    _video?.Flush();
                    PushDecoded();
                    DrainAudio(endOfStream: true);
                    if (Loop) { Rewind(0); continue; }
                    lock (_lock) _demuxDone = true;
                    continue;
                }
                int size = ReadSample(ex);
                long pts = ex.SampleTime;
                bool key = (ex.SampleFlags & MediaExtractorSampleFlags.Sync) != 0;
                if (size > 0)
                {
                    var data = new ReadOnlySpan<byte>((byte*)_sample!.GetDirectBufferAddress(), size);
                    if (track == _videoTrack) DecodeVideo(data, pts, key);
                    else if (track == _audioTrack) DecodeAudio(data, pts);
                }
                ex.Advance();
            }
        }
        catch (Exception e)
        {
            Log.Error($"AvPlayer decoder: {e.Message}");
            lock (_lock) _demuxDone = true;
        }
    }

    int ReadSample(MediaExtractor ex)
    {
        // getSampleSize is API 28; before that the largest sample the tracks declare (max-input-size)
        long need = OperatingSystem.IsAndroidVersionAtLeast(28) ? Math.Max(ex.SampleSize, 1) : _maxSample;
        if (_sample == null || _sample.Capacity() < need)
        {
            _sample?.Dispose();
            _sample = JByteBuffer.AllocateDirect((int)Math.Max(need, _maxSample));
        }
        _sample.Clear();
        return ex.ReadSampleData(_sample, 0);
    }

    void Rewind(long us)
    {
        _extractor!.SeekTo(us, MediaExtractorSeekTo.PreviousSync);
        _video?.Reset();
        try { _audio?.Flush(); } catch (Java.Lang.Exception) { }
    }

    void DecodeVideo(ReadOnlySpan<byte> sample, long pts, bool key)
    {
        int n = H264AnnexB.BuildAccessUnit(sample, key ? _csd : [], ref _au);
        _video!.Send(_au.AsSpan(0, n), pts);
        PushDecoded();
    }

    void PushDecoded()
    {
        if (_video == null) return;
        while (_video.Take() is { } p)
        {
            PushPicture(p, p.Pts);
            _video.Recycle(p);
        }
    }

    void DecodeAudio(ReadOnlySpan<byte> data, long pts)
    {
        var c = _audio!;
        try
        {
            for (int attempt = 0; attempt < 20; attempt++)
            {
                int idx = c.DequeueInputBuffer(10_000);
                if (idx < 0) { DrainAudio(false); continue; }
                using var buf = c.GetInputBuffer(idx)!;
                int n = Math.Min(data.Length, buf.Capacity());
                data[..n].CopyTo(new Span<byte>((void*)buf.GetDirectBufferAddress(), n));
                c.QueueInputBuffer(idx, 0, n, Math.Max(0, pts), 0);
                break;
            }
            DrainAudio(false);
        }
        catch (Java.Lang.Exception e) { Log.WarnOnce("avplayer-aac", $"AvPlayer: audio decoder failed: {e.Message}"); }
    }

    void DrainAudio(bool endOfStream)
    {
        if (_audio is not { } c) return;
        try
        {
            if (endOfStream)
            {
                int idx = c.DequeueInputBuffer(10_000);
                if (idx >= 0) c.QueueInputBuffer(idx, 0, 0, 0, MediaCodecBufferFlags.EndOfStream);
            }
            for (int spins = 0; spins < (endOfStream ? 100 : int.MaxValue); spins++)
            {
                int idx = c.DequeueOutputBuffer(_audioInfo, endOfStream ? 10_000 : 0);
                if (idx == -2) // INFO_OUTPUT_FORMAT_CHANGED
                {
                    var f = c.OutputFormat;
                    _audioChannels = f.GetInteger(MediaFormat.KeyChannelCount);
                    _audioRate = f.GetInteger(MediaFormat.KeySampleRate);
                    continue;
                }
                if (idx == -3) continue; // INFO_OUTPUT_BUFFERS_CHANGED
                if (idx < 0) { if (endOfStream) continue; return; }
                bool eos = (_audioInfo.Flags & MediaCodecBufferFlags.EndOfStream) != 0;
                if (_audioInfo.Size > 0 && _audioChannels > 0)
                {
                    using var buf = c.GetOutputBuffer(idx)!;
                    // 16-bit PCM, the decoders' default output encoding
                    var pcm = new ReadOnlySpan<short>((byte*)buf.GetDirectBufferAddress() + _audioInfo.Offset, _audioInfo.Size / 2);
                    var samples = RentBuffer(_freeSamples, pcm.Length);
                    pcm.CopyTo(samples);
                    Enqueue(new AudioChunk(samples, _audioChannels, _audioRate, _audioInfo.PresentationTimeUs));
                }
                c.ReleaseOutputBuffer(idx, false);
                if (eos) { c.Flush(); return; }
            }
        }
        catch (Java.Lang.Exception e) { Log.WarnOnce("avplayer-aac", $"AvPlayer: audio decoder failed: {e.Message}"); }
    }

    public override void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _stop = true;
        lock (_lock) Monitor.PulseAll(_lock);
        if (_thread.IsAlive && Thread.CurrentThread != _thread && !_thread.Join(10_000))
        {
            Log.Warn("AvPlayer decoder thread did not stop within 10 s; leaking its decoders");
            return;
        }
        _video?.Dispose();
        _video = null;
        try { _audio?.Stop(); _audio?.Release(); } catch (Java.Lang.Exception) { }
        _audio = null;
        _audioInfo.Dispose();
        _extractor?.Release();
        _extractor = null;
        _sample?.Dispose();
    }
}
