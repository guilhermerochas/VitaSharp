using System.Diagnostics;
using VitaSharp.Core.Kernel;
using VitaSharp.Core.Media;
using VitaSharp.Core.Memory;
using VitaSharp.Core.Util;
using VitaSharp.Core.SaveState;

namespace VitaSharp.Core.Hle.Modules;

/// <summary>
/// SceAvPlayer: plays MP4 (H.264 + AAC) movies. Video frames are delivered in YVU420 two-plane (NV21) buffers that
/// games bind as SCE_GXM_TEXTURE_FORMAT_YVU420P2/YUV420P2 textures; audio as interleaved S16 for sceAudioOut.
/// Structure layouts follow the shared PS4/Vita libSceAvPlayer ABI (init data 0x44 bytes, frame info 32 bytes,
/// stream info 40 bytes).
/// </summary>
public sealed partial class AvPlayer : HleModule
{
    const int ERR_ILLEGAL_ADDR = unchecked((int)0x806a0001), ERR_INVALID_ARGUMENT = unchecked((int)0x806a0002),
        ERR_NOT_ENOUGH_MEMORY = unchecked((int)0x806a0003);
    const int STATE_STOP = 1, STATE_READY = 2, STATE_PLAY = 3, STATE_PAUSE = 4, STATE_BUFFERING = 5;
    const int STREAM_VIDEO = 0, STREAM_AUDIO = 1;

    sealed partial class Player
    {
        public uint MemObject, Allocate, Deallocate, AllocateTexture, DeallocateTexture;
        public uint FileObject, FileOpen, FileClose, FileRead, FileSize;
        public uint EventObject, EventCallback;
        public int FrameBufferCount;
        public bool AutoStart;

        public readonly Queue<string> Sources = new();
        public MediaPlayerBase? Media;
        public string SourcePath = "";
        public bool Started, Paused, Looping, StopSent;

        /// <summary>The player reached its end: every queued source was played, or it was stopped. Until then the
        /// firmware's sceAvPlayerIsActive answers 1, also right after sceAvPlayerInit before any source is added
        /// (libsceavplayer: active while the player's state is not the end state).</summary>
        /// <summary>States of builds before it: IsActive answered from the open media alone, which Ended = true
        /// keeps.</summary>
        [SnapshotMissing(true)]
        public bool Ended;
        public readonly GuestStopwatch Clock = new();
        public long ClockOffsetUs;

        public uint[] VideoBuffers = [];
        public uint VideoBufferSize;
        public int VideoIndex;
        public VideoFrame? LastFrame;
        public uint LastFrameBuffer;

        public uint[] AudioBuffers = new uint[4];
        public uint[] AudioBufferSizes = new uint[4];
        public int AudioIndex;
        public long LastTimeUs;
        /// <summary>The player's clock when the last picture was handed out (-1 = none since the start, a jump or the
        /// next source); not saved: after a load the next picture is due at once.</summary>
        [SnapshotIgnore]
        public long ShownAtUs = -1;
        /// <summary>sceAvPlayerJumpToTime calls (diagnostics); not saved.</summary>
        [SnapshotIgnore]
        public int Jumps;
        /// <summary>sceAvPlayerJumpToTime calls refused while a jump was in progress (diagnostics); not saved.</summary>
        [SnapshotIgnore]
        public int RefusedJumps;
        /// <summary>Delivery statistics (the "AvPlayer: pictures" line every 10 s): GetVideoData calls, the host time of the
        /// last line, the counts then; not saved.</summary>
        [SnapshotIgnore]
        public long VideoCalls, DeliveryLogTicks, LoggedTaken, LoggedDropped, LoggedLatenessUs, LoggedCalls;

        public readonly List<string> TempFiles = new();
    }

    readonly Dictionary<uint, Player> _players = new();
    uint _nextHandle = 0x10;

    Player? Get(uint handle) { lock (_players) return _players.GetValueOrDefault(handle); }

    void Event(Player p, int id, int source = 0, uint data = 0)
    {
        if (p.EventCallback == 0) return;
        GuestThread.Current?.CallGuest(p.EventCallback, p.EventObject, (uint)id, (uint)source, data);
    }

    uint AllocGuest(Player p, uint size, bool texture)
    {
        uint fn = texture ? p.AllocateTexture : p.Allocate;
        uint align = texture ? 256u : 64u;
        if (fn != 0 && GuestThread.Current is { } t)
        {
            uint a = t.CallGuest(fn, p.MemObject, align, size);
            if (a != 0) return a;
        }
        return Emu.Memory.Alloc(size, texture ? "AvPlayer frame" : "AvPlayer audio", MemoryRegion.User, 4096);
    }

    void FreeGuest(Player p, uint addr, bool texture)
    {
        if (addr == 0) return;
        uint fn = texture ? p.DeallocateTexture : p.Deallocate;
        if (fn != 0 && GuestThread.Current is { } t) t.CallGuest(fn, p.MemObject, addr);
    }

    [Hle]
    uint sceAvPlayerInit(uint init)
    {
        if (init == 0) return 0;
        var p = new Player
        {
            MemObject = GuestMemory.R32(init), Allocate = GuestMemory.R32(init + 4), Deallocate = GuestMemory.R32(init + 8),
            AllocateTexture = GuestMemory.R32(init + 12), DeallocateTexture = GuestMemory.R32(init + 16),
            FileObject = GuestMemory.R32(init + 20), FileOpen = GuestMemory.R32(init + 24), FileClose = GuestMemory.R32(init + 28),
            FileRead = GuestMemory.R32(init + 32), FileSize = GuestMemory.R32(init + 36),
            EventObject = GuestMemory.R32(init + 40), EventCallback = GuestMemory.R32(init + 44),
            FrameBufferCount = Math.Clamp((int)GuestMemory.R32(init + 56), 2, 8),
            AutoStart = GuestMemory.Read<byte>(init + 60) != 0,
        };
        lock (_players)
        {
            uint h = _nextHandle++;
            _players[h] = p;
            return h;
        }
    }

    [Hle] int sceAvPlayerPostInit(uint handle, uint data) => Get(handle) != null ? 0 : ERR_INVALID_ARGUMENT;

    [Hle]
    int sceAvPlayerAddSource(uint handle, uint pathAddr)
    {
        var p = Get(handle);
        if (p == null) return ERR_INVALID_ARGUMENT;
        if (pathAddr == 0) return ERR_ILLEGAL_ADDR;
        string path = GuestMemory.ReadCString(pathAddr);
        string? host = Emu.Vfs.Resolve(path);
        if (host == null || !File.Exists(host)) host = ReadThroughCallbacks(p, pathAddr, path);
        if (host == null)
        {
            Log.Warn($"AvPlayer: cannot open {path}");
            return ERR_INVALID_ARGUMENT;
        }
        Log.Info($"AvPlayer: queued {path}");
        lock (p)
        {
            p.Sources.Enqueue(host);
            p.Ended = false;
            if (p.Media == null)
            {
                OpenNext(p);
            }
        }
        Event(p, STATE_BUFFERING);
        Event(p, STATE_READY);
        if (p.AutoStart) StartPlayback(p);
        return 0;
    }

    /// <summary>Games may serve the movie through their own file callbacks (packed archives): spool it to a temp file.</summary>
    string? ReadThroughCallbacks(Player p, uint pathAddr, string path)
    {
        if (p.FileOpen == 0 || p.FileRead == 0 || p.FileSize == 0 || GuestThread.Current is not { } t) return null;
        if ((int)t.CallGuest(p.FileOpen, p.FileObject, pathAddr) < 0) return null;
        try
        {
            uint size = t.CallGuest(p.FileSize, p.FileObject); // uint64 return: the low word is enough for Vita media
            if (size == 0) return null;
            const uint Chunk = 512 * 1024;
            uint buf = Emu.Memory.Alloc(Chunk, "AvPlayer read buffer");
            var tmp = TempMovies.NewPath("avplayer", $"{System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(p):x8}{p.TempFiles.Count}");
            using (var fs = File.Create(tmp))
            {
                for (uint off = 0; off < size; off += Chunk)
                {
                    uint n = Math.Min(Chunk, size - off);
                    // readOffset(object, buffer, uint64 offset, size): the 64-bit offset occupies r2:r3, size goes on the stack
                    int got = (int)t.CallGuest(p.FileRead, p.FileObject, buf, off, 0, n);
                    if (got <= 0) break;
                    fs.Write(GuestMemory.Span(buf, got));
                }
            }
            Emu.Memory.Free(buf);
            p.TempFiles.Add(tmp);
            return tmp;
        }
        finally
        {
            if (p.FileClose != 0) t.CallGuest(p.FileClose, p.FileObject);
        }
    }

    void OpenNext(Player p)
    {
        AudioDiagnostics.StreamClosed(DiagKey(p));
        p.Media?.Dispose();
        p.Media = null;
        while (p.Sources.Count > 0 && p.Media == null)
        {
            var src = p.Sources.Dequeue();
            p.SourcePath = src;
            p.Media = MediaPlayers.Open(src);
            if (p.Media == null) Log.Warn($"AvPlayer: could not decode {src}");
        }
        // nothing left that plays: the player is at its end (sceAvPlayerIsActive answers 0)
        if (p.Media == null)
        {
            p.Ended = true;
        }

        if (p.Media != null)
        {
            p.Media.Loop = p.Looping;
            p.ClockOffsetUs = 0;
            p.Clock.Reset();
            if (p.Started && !p.Paused) p.Clock.Start();
            p.StopSent = false;
            var m = p.Media;
            AudioDiagnostics.StreamOpened(DiagKey(p), $"AvPlayer {Path.GetFileName(p.SourcePath)}: " + (m.HasAudio ? $"audio {m.Channels} ch {m.SampleRate} Hz" : "no audio") +
                (m.HasVideo ? $", video {m.Width}x{m.Height} {m.FrameRate:F2} fps" : "") + $", {m.DurationUs / 1e6:F1} s");
        }
    }

    static string DiagKey(Player p) => $"avplayer:{System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(p):X8}";

    void StartPlayback(Player p)
    {
        p.Started = true;
        p.Paused = false;
        p.Clock.Start();
        Event(p, STATE_PLAY);
    }

    long ClockUs(Player p) => p.ClockOffsetUs + p.Clock.ElapsedMicroseconds;

    [Hle] int sceAvPlayerStart(uint handle) { var p = Get(handle); if (p == null) return ERR_INVALID_ARGUMENT; StartPlayback(p); return 0; }

    [Hle]
    int sceAvPlayerStop(uint handle)
    {
        var p = Get(handle);
        if (p == null) return ERR_INVALID_ARGUMENT;
        lock (p)
        {
            AudioDiagnostics.StreamClosed(DiagKey(p));
            p.Media?.Dispose();
            p.Media = null;
            p.Sources.Clear();
            p.Ended = true;
        }
        p.Started = false;
        p.Clock.Reset();
        Event(p, STATE_STOP);
        p.StopSent = true;
        return 0;
    }

    [Hle]
    int sceAvPlayerPause(uint handle)
    {
        var p = Get(handle);
        if (p == null) return ERR_INVALID_ARGUMENT;
        p.Paused = true;
        p.Clock.Stop();
        Event(p, STATE_PAUSE);
        return 0;
    }

    [Hle]
    int sceAvPlayerResume(uint handle)
    {
        var p = Get(handle);
        if (p == null) return ERR_INVALID_ARGUMENT;
        if (p.Paused)
        {
            p.Paused = false;
            if (p.Started) p.Clock.Start();
            Event(p, STATE_PLAY);
        }
        return 0;
    }

    [Hle]
    int sceAvPlayerSetLooping(uint handle, int loop)
    {
        var p = Get(handle);
        if (p == null) return ERR_INVALID_ARGUMENT;
        p.Looping = loop != 0;
        if (p.Media != null) p.Media.Loop = p.Looping;
        return 0;
    }

    [Hle] int sceAvPlayerSetTrickSpeed(uint handle, int speed) => Get(handle) != null ? 0 : ERR_INVALID_ARGUMENT;

    [Hle]
    int sceAvPlayerJumpToTime(uint handle, ulong timeMs)
    {
        var p = Get(handle);
        if (p == null) return ERR_INVALID_ARGUMENT;
        long us = (long)timeMs * 1000;
        if (p.Media is { JumpInProgress: true })
        {
            // libsceavplayer refuses a jump while the previous one has not reached its time ("Jump attempted while
            // still in jump State"): the caller gets 0x806a0002 and the movie keeps going to the first jump's time
            p.RefusedJumps++;
            return ERR_INVALID_ARGUMENT;
        }

        p.Jumps++;
        if (p.Jumps <= 30 || p.Jumps % 100 == 0)
        {
            // games keep a movie in step with its own audio by jumping: how often and how far behind the video was
            Log.Info($"AvPlayer: jump {p.Jumps} to {timeMs} ms in {Path.GetFileName(p.SourcePath)} (clock {ClockUs(p) / 1000} ms, last picture {p.LastTimeUs / 1000} ms, {p.RefusedJumps} refused while one was in progress)");
        }

        p.Media?.Seek(us);
        if (!MediaPlayerBase.LegacyJumps)
        {
            // CurrentTime reads the player's sync clock, which a jump sets to its time: Killzone: Mercenary holds its
            // title and campaign movies on a short loop by jumping back whenever CurrentTime passes the loop's end;
            // still answering the last picture's time, it jumped again on every frame (1500 jumps in 50 s on the
            // phone) and the decoder was flushed 30 times a second
            p.LastTimeUs = us;
            // and sceAvPlayerGetVideoData gives no picture while the player is in its jump state (libsceavplayer
            // answers 0 outside its play states): the picture from before the jump, with its old timestamp, made the
            // game see the loop's end again and jump on the next frame
            p.LastFrame = null;
        }

        p.ClockOffsetUs = us;
        bool running = p.Clock.IsRunning;
        p.Clock.Reset();
        if (running) p.Clock.Start();
        return 0;
    }

    /// <summary>
    /// The player's sync clock as libsceavplayer 3.74 answers it for a movie without sound (the video is its own clock):
    /// the time of the last picture handed out, or the time a jump set. Killzone: Mercenary asks for a picture only
    /// when this is more than ~200 ms behind its own time, so the answer moves one picture per call with the firmware's
    /// one-picture pacing (see NextPictureDue); a clock that ran on its own kept it from asking at all (64 pictures in
    /// 150 s in a desktop re-run of the campaign intro, 14/11/2026). With sound the firmware answers the time of the
    /// last sound handed out; here it is still the last picture's (docs/roadmap.md).
    /// </summary>
    [Hle]
    ulong sceAvPlayerCurrentTime(uint handle)
    {
        if (Get(handle) is not { } p)
        {
            return 0;
        }

        return (ulong)Math.Max(0, p.LastTimeUs / 1000);
    }

    /// <summary>1 until the player reaches its end (all sources played, or stopped); also 1 right after
    /// sceAvPlayerInit, before the first source: Killzone: Mercenary starts its briefing movie, checks IsActive and
    /// adds the source on the next update, and closed the movie at once when this answered 0 there.</summary>
    [Hle]
    int sceAvPlayerIsActive(uint handle)
    {
        Player? p = Get(handle);
        if (p == null)
        {
            return 0;
        }

        lock (p) // the media is closed by whichever guest thread gets there first (IsActive, Stop, Close)
        {
            if (p.Media != null && p.Media.Finished && !p.Looping)
            {
                if (p.Sources.Count > 0)
                {
                    OpenNext(p);
                }
                else
                {
                    AudioDiagnostics.StreamClosed(DiagKey(p));
                    p.Media.Dispose();
                    p.Media = null;
                    p.Ended = true;
                }
            }
        }

        bool active = p.Media != null || !p.Ended;
        if (!active && p.Started && !p.StopSent)
        {
            p.StopSent = true;
            Event(p, STATE_STOP);
        }
        return active ? 1 : 0;
    }

    [Hle] int sceAvPlayerStreamCount(uint handle) => Get(handle)?.Media is { } m ? (m.HasVideo ? 1 : 0) + (m.HasAudio ? 1 : 0) : 0;

    int StreamType(MediaPlayerBase m, uint index) => index == 0 ? (m.HasVideo ? STREAM_VIDEO : STREAM_AUDIO) : index == 1 && m.HasVideo && m.HasAudio ? STREAM_AUDIO : -1;

    [Hle]
    int sceAvPlayerGetStreamInfo(uint handle, uint index, uint info)
    {
        if (info == 0) return ERR_ILLEGAL_ADDR;
        if (Get(handle)?.Media is not { } m) return ERR_INVALID_ARGUMENT;
        int type = StreamType(m, index);
        if (type < 0) return ERR_INVALID_ARGUMENT;
        GuestMemory.Fill(info, 0, 40);
        GuestMemory.W32(info, (uint)type);
        if (type == STREAM_VIDEO) WriteVideoDetails(info + 8, m.Width, m.Height);
        else WriteAudioDetails(info + 8, m.Channels, m.SampleRate, 0);
        GuestMemory.Write(info + 24, (ulong)(m.DurationUs / 1000)); // duration (ms)
        GuestMemory.Write(info + 32, 0UL);                           // start time
        return 0;
    }

    [Hle] int sceAvPlayerEnableStream(uint handle, uint index) => Get(handle) is { Media: { } m } && StreamType(m, index) >= 0 ? 0 : ERR_INVALID_ARGUMENT;
    [Hle] int sceAvPlayerDisableStream(uint handle, uint index) => Get(handle) != null ? 0 : ERR_INVALID_ARGUMENT;

    static void WriteVideoDetails(uint d, int w, int h)
    {
        GuestMemory.W32(d, (uint)w);
        GuestMemory.W32(d + 4, (uint)h);
        GuestMemory.Write(d + 8, h > 0 ? (float)w / h : 1f);
        GuestMemory.WriteCString(d + 12, "ENG", 4);
    }

    static void WriteAudioDetails(uint d, int channels, int rate, uint size)
    {
        GuestMemory.Write(d, (ushort)channels);
        GuestMemory.Write(d + 2, (ushort)0);
        GuestMemory.W32(d + 4, (uint)rate);
        GuestMemory.W32(d + 8, size);
        GuestMemory.WriteCString(d + 12, "ENG", 4);
    }

    [Hle] int sceAvPlayerGetVideoDataEx(uint handle, uint frameInfo) => sceAvPlayerGetVideoDataThroughEntry(handle, frameInfo);

    /// <summary>Returns true with the frame due at the current playback time (or the last frame while none is due yet).</summary>
    [Hle]
    int sceAvPlayerGetVideoData(uint handle, uint frameInfo)
    {
        var p = Get(handle);
        if (p == null || frameInfo == 0 || p.Media is not { HasVideo: true } m || !p.Started || p.Paused) return 0;
        long clockUs = ClockUs(p);
        VideoFrame? frame;
        if (MediaPlayerBase.NewestPicturePacing)
        {
            frame = m.TakeVideo(clockUs);
        }
        else
        {
            frame = NextPictureDue(p, m, clockUs) ? m.TakeNextVideo(clockUs) : null;
        }

        p.VideoCalls++;
        LogDelivery(p, m, clockUs);
        if (frame != null)
        {
            uint size = (uint)frame.Nv12.Length;
            if (p.VideoBuffers.Length == 0 || p.VideoBufferSize < size)
            {
                foreach (var b in p.VideoBuffers) FreeGuest(p, b, true);
                p.VideoBuffers = new uint[Math.Max(p.FrameBufferCount, 3)];
                for (int i = 0; i < p.VideoBuffers.Length; i++) p.VideoBuffers[i] = AllocGuest(p, size, true);
                p.VideoBufferSize = size;
            }
            uint buf = p.VideoBuffers[p.VideoIndex++ % p.VideoBuffers.Length];
            frame.Nv12.CopyTo(GuestMemory.Span(buf, (int)size));
            m.Recycle(frame); // only its timestamp and size are read from here on
            p.LastFrame = frame;
            p.LastFrameBuffer = buf;
            p.LastTimeUs = frame.TimeUs;
            p.ShownAtUs = clockUs;
        }
        if (p.LastFrame == null) return 0;
        GuestMemory.Fill(frameInfo, 0, 32);
        GuestMemory.W32(frameInfo, p.LastFrameBuffer);
        GuestMemory.Write(frameInfo + 8, (ulong)(p.LastFrame.TimeUs / 1000));
        WriteVideoDetails(frameInfo + 16, p.LastFrame.Width, p.LastFrame.Height);
        return 1;
    }

    /// <summary>
    /// libsceavplayer 3.74's choice in GetVideoData: one picture per call, the next one in its queue, also when it is
    /// late (it never skips to the newest); it is due once the stream clock has reached it (the firmware's sync check;
    /// here the picture's time against the player's clock). Before the first picture (start, jump, next source) the
    /// next one is due at once. Killzone: Mercenary calls GetVideoData in a loop while CurrentTime is more than ~200 ms
    /// behind its own time: a gate of 20 ms since the last picture made that loop spin thousands of times a second
    /// after a slow frame (desktop re-run of the campaign intro, 14/11/2026), the picture's time lets it catch up.
    /// </summary>
    static bool NextPictureDue(Player p, MediaPlayerBase m, long clockUs)
    {
        if (p.LastFrame == null || p.ShownAtUs < 0 || p.ShownAtUs > clockUs)
        {
            return true;
        }

        long nextUs = m.PeekVideoTime();
        return nextUs >= 0 && nextUs <= clockUs;
    }

    /// <summary>Every 10 s of host time: the pictures handed out and dropped as late, and how late, so a movie whose
    /// picture changes less often than the game presents shows in the log (Killzone: Mercenary's campaign intro on the
    /// phone changed 3-6 times a second at 30 fps, 14/11/2026).</summary>
    static void LogDelivery(Player p, MediaPlayerBase m, long clockUs)
    {
        long now = Stopwatch.GetTimestamp();
        if (p.DeliveryLogTicks == 0)
        {
            p.DeliveryLogTicks = now;
            return;
        }

        if (now - p.DeliveryLogTicks < Stopwatch.Frequency * 10)
        {
            return;
        }

        double seconds = (now - p.DeliveryLogTicks) / (double)Stopwatch.Frequency;
        long taken = m.PicturesTaken - p.LoggedTaken, dropped = m.PicturesDroppedLate - p.LoggedDropped;
        long lateness = m.LatenessUs - p.LoggedLatenessUs, calls = p.VideoCalls - p.LoggedCalls;
        Log.Info($"AvPlayer: pictures {Path.GetFileName(p.SourcePath)}: {taken / seconds:F1}/s handed out, {dropped / seconds:F1}/s dropped as late, "
            + $"{(taken > 0 ? lateness / taken / 1000.0 : 0):F1} ms late on average, {calls / seconds:F0} GetVideoData/s, {m.QueuedPictures} queued, "
            + $"clock {clockUs / 1000} ms, last picture {p.LastTimeUs / 1000} ms");
        p.DeliveryLogTicks = now;
        p.LoggedTaken = m.PicturesTaken;
        p.LoggedDropped = m.PicturesDroppedLate;
        p.LoggedLatenessUs = m.LatenessUs;
        p.LoggedCalls = p.VideoCalls;
    }

    /// <summary>Audio is handed out as soon as it is decoded: the game's sceAudioOut calls provide the pacing.</summary>
    [Hle]
    int sceAvPlayerGetAudioData(uint handle, uint frameInfo)
    {
        var p = Get(handle);
        if (p == null || frameInfo == 0 || p.Media is not { HasAudio: true } m || !p.Started || p.Paused) return 0;
        var chunk = m.TakeAudio();
        if (chunk == null) return 0;
        uint size = (uint)chunk.Samples.Length * 2;
        int slot = p.AudioIndex++ % p.AudioBuffers.Length;
        if (p.AudioBufferSizes[slot] < size)
        {
            FreeGuest(p, p.AudioBuffers[slot], false);
            p.AudioBuffers[slot] = AllocGuest(p, Math.Max(size, 16384), false);
            p.AudioBufferSizes[slot] = Math.Max(size, 16384);
        }
        uint buf = p.AudioBuffers[slot];
        System.Runtime.InteropServices.MemoryMarshal.AsBytes(chunk.Samples.AsSpan()).CopyTo(GuestMemory.Span(buf, (int)size));
        m.Recycle(chunk);
        if (!m.HasVideo) p.LastTimeUs = chunk.TimeUs;
        GuestMemory.Fill(frameInfo, 0, 32);
        GuestMemory.W32(frameInfo, buf);
        GuestMemory.Write(frameInfo + 8, (ulong)(chunk.TimeUs / 1000));
        WriteAudioDetails(frameInfo + 16, chunk.Channels, chunk.SampleRate, size);
        return 1;
    }

    [Hle]
    int sceAvPlayerClose(uint handle)
    {
        Player? p;
        lock (_players)
        {
            if (!_players.Remove(handle, out p)) return ERR_INVALID_ARGUMENT;
        }
        AudioDiagnostics.StreamClosed(DiagKey(p));
        lock (p) { p.Media?.Dispose(); p.Media = null; }
        foreach (var b in p.VideoBuffers) FreeGuest(p, b, true);
        foreach (var b in p.AudioBuffers) FreeGuest(p, b, false);
        foreach (var f in p.TempFiles)
        {
            try { File.Delete(f); } catch (IOException) { }
        }
        return 0;
    }
}
