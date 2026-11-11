namespace VitaSharp.Core.Media;

/// <summary>
/// The consumer side of SceAvPlayer's media player: a decoder thread (in the subclass) fills small bounded queues of
/// NV21 video frames and 16-bit audio chunks, so the guest's GetVideoData/GetAudioData calls never wait for the
/// decoder. Subclasses: <see cref="MediaPlayer"/> (FFmpeg demuxing and audio, H.264 through the pluggable
/// <see cref="IVideoDecoder"/>) and, on Android, a MediaExtractor/MediaCodec player (src/VitaSharp.Android/Media),
/// installed with <see cref="MediaPlayers.Backend"/>.
/// </summary>
public abstract class MediaPlayerBase : IDisposable
{
    protected const int MaxVideoFrames = 12;
    protected const int MaxAudioChunks = 48;

    protected readonly Queue<VideoFrame> VideoQueue = new();
    protected readonly Queue<AudioChunk> AudioQueue = new();
    protected readonly object SyncRoot = new();
    protected volatile bool StopRequested;
    /// <summary>Pending seek target (µs), -1 = none; guarded by <see cref="SyncRoot"/>.</summary>
    protected long SeekRequestUs = -1;
    /// <summary>The whole file was decoded (guarded by <see cref="SyncRoot"/>).</summary>
    protected bool DemuxDone;

    public int Width { get; protected set; }
    public int Height { get; protected set; }
    public double FrameRate { get; protected set; }
    public int Channels { get; protected set; }
    public int SampleRate { get; protected set; }
    public long DurationUs { get; protected set; }
    public abstract bool HasVideo { get; }
    public abstract bool HasAudio { get; }
    public bool Loop { get; set; }

    // ------------------------------------------------------------------ consumer side

    /// <summary>Next video frame with a timestamp at or before <paramref name="clockUs"/> (dropping late frames).</summary>
    public VideoFrame? TakeVideo(long clockUs)
    {
        lock (SyncRoot)
        {
            VideoFrame? f = null;
            while (VideoQueue.Count > 0 && VideoQueue.Peek().TimeUs <= clockUs)
            {
                if (f != null)
                {
                    ReturnBuffer(FreeNv12, f.Nv12); // dropped late frame
                }

                f = VideoQueue.Dequeue();
            }
            if (f != null)
            {
                Monitor.PulseAll(SyncRoot);
            }

            return f;
        }
    }

    // decoded planes/samples handed back by the consumer once copied out (no garbage per video frame or audio chunk);
    // guarded by SyncRoot, at most a few of each are kept
    protected readonly Stack<byte[]> FreeNv12 = new();
    protected readonly Stack<short[]> FreeSamples = new();

    protected T[] RentBuffer<T>(Stack<T[]> free, int length)
    {
        lock (SyncRoot)
        {
            while (free.Count > 0)
            {
                if (free.Pop() is var a && a.Length == length)
                {
                    return a; // buffers of another size are dropped
                }
            }
        }

        return new T[length];
    }

    protected void ReturnBuffer<T>(Stack<T[]> free, T[] buffer)
    {
        lock (SyncRoot)
        {
            if (free.Count < 4)
            {
                free.Push(buffer);
            }
        }
    }

    /// <summary>The frame's planes were copied out and are not read again: reuse them for a later frame.</summary>
    public void Recycle(VideoFrame f) => ReturnBuffer(FreeNv12, f.Nv12);

    /// <summary>The chunk's samples were copied out and are not read again.</summary>
    public void Recycle(AudioChunk c) => ReturnBuffer(FreeSamples, c.Samples);

    public AudioChunk? TakeAudio()
    {
        lock (SyncRoot)
        {
            if (AudioQueue.Count == 0)
            {
                return null;
            }

            AudioChunk c = AudioQueue.Dequeue();
            Monitor.PulseAll(SyncRoot);
            return c;
        }
    }

    /// <summary>Timestamp of the next queued video frame, or -1.</summary>
    public long PeekVideoTime()
    {
        lock (SyncRoot)
        {
            return VideoQueue.Count > 0 ? VideoQueue.Peek().TimeUs : -1;
        }
    }

    /// <summary>True once the file has been fully decoded and every queued frame consumed.</summary>
    public bool Finished
    {
        get
        {
            lock (SyncRoot)
            {
                return DemuxDone && VideoQueue.Count == 0 && AudioQueue.Count == 0;
            }
        }
    }

    public void Seek(long timeUs)
    {
        lock (SyncRoot)
        {
            SeekRequestUs = Math.Max(0, timeUs);
            VideoQueue.Clear();
            AudioQueue.Clear();
            DemuxDone = false;
            Monitor.PulseAll(SyncRoot);
        }
    }

    // ------------------------------------------------------------------ producer side (decoder thread)

    /// <summary>Waits while both queues are full enough (or the file is done) and no seek or stop is pending.</summary>
    protected void WaitForRoom()
    {
        lock (SyncRoot)
        {
            while (!StopRequested && SeekRequestUs < 0 && (DemuxDone || (HasVideo ? VideoQueue.Count >= MaxVideoFrames : AudioQueue.Count >= MaxAudioChunks)
                                                   || AudioQueue.Count >= MaxAudioChunks))
            {
                Monitor.Wait(SyncRoot, 50);
            }
        }
    }

    /// <summary>Planar YUV 4:2:0 from a decoder into the NV21 frame layout the guest receives.</summary>
    protected void PushPicture(DecodedPicture p, long timeUs)
    {
        int w = p.Width & ~1, h = p.Height & ~1, cw = (p.Width + 1) / 2;
        byte[] nv21 = RentBuffer(FreeNv12, w * h * 3 / 2);
        for (int y = 0; y < h; y++)
        {
            p.Y.AsSpan(y * p.Width, w).CopyTo(nv21.AsSpan(y * w));
        }

        int uvBase = w * h;
        for (int y = 0; y < h / 2; y++)
        {
            int s = y * cw, d = uvBase + y * w;
            for (int x = 0; x < w / 2; x++)
            {
                nv21[d + 2 * x] = p.V[s + x];
                nv21[d + 2 * x + 1] = p.U[s + x];
            }
        }
        Enqueue(new VideoFrame(nv21, w, h, timeUs));
    }

    protected void Enqueue(VideoFrame vf)
    {
        lock (SyncRoot)
        {
            while (!StopRequested && SeekRequestUs < 0 && VideoQueue.Count >= MaxVideoFrames)
            {
                Monitor.Wait(SyncRoot, 50);
            }

            if (SeekRequestUs < 0)
            {
                VideoQueue.Enqueue(vf);
            }
        }
    }

    protected void Enqueue(AudioChunk chunk)
    {
        lock (SyncRoot)
        {
            while (!StopRequested && SeekRequestUs < 0 && AudioQueue.Count >= MaxAudioChunks)
            {
                Monitor.Wait(SyncRoot, 50);
            }

            if (SeekRequestUs < 0)
            {
                AudioQueue.Enqueue(chunk);
            }
        }
    }

    public abstract void Dispose();
}

/// <summary>Opens SceAvPlayer media files. Front-ends without FFmpeg (Android) install their own player.</summary>
public static class MediaPlayers
{
    /// <summary>Opens a file, or null when this backend cannot play it (then FFmpeg is tried, when it is available).</summary>
    public static Func<string, MediaPlayerBase?>? Backend { get; set; }

    public static MediaPlayerBase? Open(string path) => Backend?.Invoke(path) ?? MediaPlayer.Open(path);
}
