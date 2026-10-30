using FFmpeg.AutoGen;
using VitaSharp.Core.Kernel;
using VitaSharp.Core.Media;
using VitaSharp.Core.Memory;
using VitaSharp.Core.Util;

namespace VitaSharp.Core.Hle.Modules;

/// <summary>
/// SceMp4: MP4 demuxer library (units are decoded by the game with SceAvcdec / its own AAC decoder). The API is
/// undocumented; layouts were recovered from F1 2011's movie player:
///  - OpenFile(path, fileCallbacks{arg, open, close, readOffset, size}, memConfig, ...) -> handle
///  - StartFileStreaming(h, int *streamCount, uint64 *duration)
///  - GetStreamInfo(h, index, info[0x158]): +0 type (0 video, 1 audio), +4 codec (video 1 = AVC; audio 2 = AAC),
///    video +8 width, +0xC height, +0x30 duration; audio +0x38 sample rate, +0x4C channels; +0x50 language[4]
///  - GetNextUnit(h, unit[0x38]): +0 data, +8 size, +0x10 pts, +0x18 dts, +0x28 sync, +0x2C stream, +0x30 type
/// Timestamps are 90 kHz ticks; PTSToTime/TimeToPTS convert to microseconds.
/// </summary>
public sealed class Mp4 : HleModule
{
    // error values are not documented; games only test for < 0 (end of stream ends playback)
    const int ERR_INVALID_HANDLE = unchecked((int)0x80630002), ERR_END_OF_STREAM = unchecked((int)0x80630010),
        ERR_NOT_FOUND = unchecked((int)0x80630005), ERR_INVALID_ARG = unchecked((int)0x80630001);

    sealed class File
    {
        public IMp4Demuxer Demux = null!;
        public readonly HashSet<int> Enabled = new();
        public DemuxedUnit? Last;
        public uint Buffer, BufferSize;
        public string TempPath = "";
    }

    readonly Dictionary<int, File> _files = new();
    int _nextHandle = 1;

    File? Get(int h) { lock (_files) return _files.GetValueOrDefault(h); }

    [Hle]
    int sceMp4OpenFile(string path, uint fileCallbacks, uint memConfig, uint a3, uint a4, uint a5, uint type)
    {
        Log.Debug($"sceMp4OpenFile({path}, cb 0x{fileCallbacks:X8}, mem 0x{memConfig:X8}, {a3:X}, {a4:X}, {a5:X}, {type})");
        string? host = Emu.Vfs.Resolve(path);
        string temp = "";
        if ((host == null || !System.IO.File.Exists(host)) && fileCallbacks != 0)
        {
            host = ReadThroughCallbacks(fileCallbacks, path);
            temp = host ?? "";
        }
        var demux = host != null ? Mp4Demuxers.Open(host) : null;
        if (demux == null)
        {
            Log.Warn($"sceMp4OpenFile: cannot open {path}");
            return 0;
        }
        var f = new File { Demux = demux, TempPath = temp };
        for (int i = 0; i < demux.Streams.Count; i++) f.Enabled.Add(i);
        lock (_files) { _files[_nextHandle] = f; return _nextHandle++; }
    }

    /// <summary>Copies the movie through the game's file callbacks {arg, open, close, readOffset, size} into a temp file.</summary>
    string? ReadThroughCallbacks(uint cb, string path)
    {
        if (GuestThread.Current is not { } t) return null;
        uint arg = GuestMemory.R32(cb), open = GuestMemory.R32(cb + 4), close = GuestMemory.R32(cb + 8);
        uint read = GuestMemory.R32(cb + 12), size = GuestMemory.R32(cb + 16);
        if (open == 0 || read == 0 || size == 0) return null;
        uint pathBuf = Emu.Memory.Alloc((uint)path.Length + 1, "mp4 path");
        GuestMemory.WriteCString(pathBuf, path);
        try
        {
            if ((int)t.CallGuest(open, arg, pathBuf) < 0) return null;
            uint total = t.CallGuest(size, arg);
            const uint Chunk = 256 * 1024;
            uint buf = Emu.Memory.Alloc(Chunk, "mp4 read");
            var tmp = Path.Combine(Path.GetTempPath(), $"vitasharp-mp4-{Environment.ProcessId}-{Guid.NewGuid():N}.mp4");
            using (var fs = System.IO.File.Create(tmp))
            {
                for (uint off = 0; off < total; off += Chunk)
                {
                    uint n = Math.Min(Chunk, total - off);
                    // readOffset(arg, buffer, uint64 offset, size)
                    if ((int)t.CallGuest(read, arg, buf, off, 0, n) < 0) break;
                    fs.Write(GuestMemory.Span(buf, (int)n));
                }
            }
            Emu.Memory.Free(buf);
            if (close != 0) t.CallGuest(close, arg);
            return tmp;
        }
        finally { Emu.Memory.Free(pathBuf); }
    }

    [Hle]
    int sceMp4CloseFile(int h)
    {
        File? f;
        lock (_files) if (!_files.Remove(h, out f)) return ERR_INVALID_HANDLE;
        f.Demux.Dispose();
        if (f.Buffer != 0) Emu.Memory.Free(f.Buffer);
        if (f.TempPath.Length > 0) try { System.IO.File.Delete(f.TempPath); } catch (IOException) { }
        return 0;
    }

    [Hle]
    int sceMp4StartFileStreaming(int h, Ptr<int> streamCount, Ptr<ulong> duration)
    {
        if (Get(h) is not { } f) return ERR_INVALID_HANDLE;
        streamCount.TrySet(f.Demux.Streams.Count);
        duration.TrySet((ulong)f.Demux.Duration90k);
        return 0;
    }

    [Hle]
    int sceMp4GetStreamInfo(int h, int index, uint info)
    {
        if (Get(h) is not { } f) return ERR_INVALID_HANDLE;
        if (index < 0 || index >= f.Demux.Streams.Count || info == 0) return ERR_INVALID_ARG;
        var s = f.Demux.Streams[index];
        GuestMemory.Fill(info, 0, 0x158);
        GuestMemory.W32(info, s.Video ? 0u : 1u);
        if (s.Video)
        {
            GuestMemory.W32(info + 4, s.Codec == AVCodecID.AV_CODEC_ID_H264 ? 1u : 0u);
            GuestMemory.W32(info + 8, (uint)s.Width);
            GuestMemory.W32(info + 12, (uint)s.Height);
            uint fps1000 = (uint)Math.Round(s.FrameRate * 1000);
            GuestMemory.W32(info + 0x10, fps1000 / 1000);
            GuestMemory.W32(info + 0x14, fps1000 % 1000);
            GuestMemory.W32(info + 0x20, 90000);
        }
        else
        {
            GuestMemory.W32(info + 4, s.Codec == AVCodecID.AV_CODEC_ID_AAC ? 2u : 0u);
            GuestMemory.W32(info + 0x38, (uint)s.SampleRate);
            GuestMemory.W32(info + 0x3C, (uint)s.BitRate);
            GuestMemory.W32(info + 0x4C, (uint)s.Channels);
        }
        GuestMemory.Write(info + 0x30, (ulong)s.Duration90k);
        var lang = System.Text.Encoding.ASCII.GetBytes((s.Language + "\0\0\0\0")[..4]);
        lang.CopyTo(GuestMemory.Span(info + 0x50, 4));
        return 0;
    }

    [Hle]
    int sceMp4EnableStream(int h, int index, int enable)
    {
        if (Get(h) is not { } f) return ERR_INVALID_HANDLE;
        if (enable != 0) f.Enabled.Add(index); else f.Enabled.Remove(index);
        return 0;
    }

    [Hle]
    int sceMp4Reset(int h)
    {
        if (Get(h) is not { } f) return ERR_INVALID_HANDLE;
        f.Demux.Seek(0);
        return 0;
    }

    [Hle]
    int sceMp4JumpPTS(int h, ulong pts)
    {
        if (Get(h) is not { } f) return ERR_INVALID_HANDLE;
        f.Demux.Seek((long)pts);
        return 0;
    }

    [Hle] ulong sceMp4PTSToTime(ulong pts) => pts * 100 / 9;   // 90 kHz -> µs
    [Hle] ulong sceMp4TimeToPTS(ulong time) => time * 9 / 100;

    [Hle("sceMp4GetNextUnit")]
    [Hle("sceMp4GetNextUnit2Ref")]
    [Hle("sceMp4GetNextUnit3Ref")]
    int sceMp4GetNextUnit(int h, uint unit)
    {
        if (Get(h) is not { } f) return ERR_INVALID_HANDLE;
        var u = f.Demux.Next(s => f.Enabled.Contains(s));
        f.Last = u;
        if (u == null)
        {
            Log.Debug($"sceMp4: end of stream (handle {h})");
            return ERR_END_OF_STREAM;
        }
        if (f.BufferSize < u.Data.Length)
        {
            if (f.Buffer != 0) Emu.Memory.Free(f.Buffer);
            f.BufferSize = (uint)Math.Max(u.Data.Length, 256 * 1024);
            f.Buffer = Emu.Memory.Alloc(f.BufferSize, "mp4 unit");
        }
        u.Data.CopyTo(GuestMemory.Span(f.Buffer, u.Data.Length));
        if (unit != 0)
        {
            GuestMemory.Fill(unit, 0, 0x38);
            GuestMemory.W32(unit, f.Buffer);
            GuestMemory.W32(unit + 8, (uint)u.Data.Length);
            GuestMemory.Write(unit + 0x10, (ulong)u.Pts90k);
            GuestMemory.Write(unit + 0x18, (ulong)u.Dts90k);
            GuestMemory.W32(unit + 0x28, u.Key ? 1u : 0u);
            GuestMemory.W32(unit + 0x2C, (uint)u.Stream);
            GuestMemory.Write(unit + 0x30, (byte)(f.Demux.Streams[u.Stream].Video ? 0 : 1));
        }
        return 0;
    }

    /// <summary>Copies the data of the unit returned by the last GetNextUnit into the caller's buffer.</summary>
    [Hle]
    int sceMp4GetNextUnitData(int h, uint buffer, uint size)
    {
        if (Get(h) is not { } f) return ERR_INVALID_HANDLE;
        if (f.Last == null) return ERR_END_OF_STREAM;
        int n = (int)Math.Min((uint)f.Last.Data.Length, size == 0 ? (uint)f.Last.Data.Length : size);
        if (buffer != 0) f.Last.Data.AsSpan(0, n).CopyTo(GuestMemory.Span(buffer, n));
        return n;
    }

    [Hle] int sceMp4ReleaseBuffer(int h, uint unit) => Get(h) != null ? 0 : ERR_INVALID_HANDLE;

    // exported without known names; F1 2011 calls them with the handle first
    [Hle(Nid = 0x94E1305D)] int Mp4_94E1305D(int h, uint a, uint b) { Log.DebugOnce("mp4-94E1305D", $"sceMp4 NID 94E1305D({h}, {a:X}, {b:X})"); return 0; }
    [Hle(Nid = 0xEFE299C9)] int Mp4_EFE299C9(int h, uint a, uint b) { Log.DebugOnce("mp4-EFE299C9", $"sceMp4 NID EFE299C9({h}, {a:X}, {b:X})"); return 0; }
    [Hle(Nid = 0xA870587F)] int Mp4_A870587F(int h, uint a, uint b) { Log.DebugOnce("mp4-A870587F", $"sceMp4 NID A870587F({h}, {a:X}, {b:X})"); return 0; }
    [Hle(Nid = 0xD2871F1C)] int Mp4_D2871F1C(int h, uint a, uint b) { Log.DebugOnce("mp4-D2871F1C", $"sceMp4 NID D2871F1C({h}, {a:X}, {b:X})"); return 0; }
    [Hle(Nid = 0xCB91CEBC)] int Mp4_CB91CEBC(int h, uint a, uint b) { Log.DebugOnce("mp4-CB91CEBC", $"sceMp4 NID CB91CEBC({h}, {a:X}, {b:X})"); return 0; }
}
