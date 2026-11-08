using VitaSharp.Core.Memory;
using VitaSharp.Core.Util;

namespace VitaSharp.Core.Hle.Modules;

/// <summary>
/// SceVideoencUser / SceAvcenc: hardware H.264 encoder (video recording: the Camera, SceLibMp4Recorder). There is no
/// vitasdk header and no Vita3K module: prototypes are inferred from the SceVideodec/SceAvcdec pair (Videodec.cs) and
/// the picture layouts from the Camera (NPXS10004, FW 3.74), which encodes through <see cref="sceAvcencEncode"/> and
/// takes each NAL unit with <see cref="sceAvcencGetNalUnit"/> (layouts there). The pictures are encoded with FFmpeg's
/// H.264 encoder (<see cref="Media.H264Encoder"/>); without one, no picture is produced. Error codes follow
/// SceVideodec (INVALID_PARAM / INVALID_POINTER) until confirmed.
/// </summary>
public sealed partial class Videoenc : HleModule
{
    internal const int ERR_INVALID_PARAM = unchecked((int)0x80620002), ERR_INVALID_POINTER = unchecked((int)0x80620009);
    /// <summary>Probable SCE_VIDEOENC_TYPE_HW_AVCENC (by analogy with TYPE_HW_AVCDEC = 0x1001); any type is accepted.</summary>
    internal const uint TYPE_HW_AVCENC_GUESS = 0x2001;
    const uint LibraryMemSize = 0x40000, MaxWidth = 1920, MaxHeight = 1088;

    sealed class Encoder
    {
        public uint Width, Height, Preset;
        public long Frames, Pictures;
        public bool Stopped, Tried;
        public int FrameRate = 30, BitRate = 3_000_000, Gop = 30;
        public Media.H264Encoder? Codec;
        /// <summary>The NAL units of the last picture: guest address (after the start code), size, nal_unit_type.</summary>
        public readonly List<(uint Address, uint Size, byte Type)> Nals = new();
    }

    readonly object _lock = new();
    readonly HashSet<uint> _libraries = new();
    readonly Dictionary<uint, Encoder> _encoders = new();
    uint _nextHandle = 1;

    void CheckType(uint codecType)
    {
        if (codecType != TYPE_HW_AVCENC_GUESS)
            Log.WarnOnce("videoenc-type" + codecType, $"sceVideoenc: codec type 0x{codecType:X} accepted (type values unconfirmed)");
    }

    int InitLibrary(uint codecType)
    {
        CheckType(codecType);
        lock (_lock) _libraries.Add(codecType);
        return 0;
    }

    [Hle] int sceVideoencInitLibrary(uint codecType, uint initInfo) => InitLibrary(codecType);
    [Hle] int sceVideoencInitLibraryInternal(uint codecType, uint initInfo) => InitLibrary(codecType);
    /// <summary>The camera (NPXS10004) calls it as (type, memInfo {+0xC address, +0x10 size in its unmap block}, 0,
    /// initInfo); games as (type, initInfo, memInfo). Either memory pointer is enough.</summary>
    [Hle] int sceVideoencInitLibraryWithUnmapMem(uint codecType, uint a1, uint a2, uint a3) => a1 == 0 && a2 == 0 ? ERR_INVALID_POINTER : InitLibrary(codecType);
    [Hle] int sceVideoencInitLibraryWithUnmapMemInternal(uint codecType, uint a1, uint a2, uint a3) => a1 == 0 && a2 == 0 ? ERR_INVALID_POINTER : InitLibrary(codecType);

    /// <summary>Writes the library memory size at memInfo+0 (layout as in sceVideodecQueryMemSize).</summary>
    int QueryMemSize(uint codecType, uint memInfo)
    {
        CheckType(codecType);
        if (memInfo == 0) return ERR_INVALID_POINTER;
        GuestMemory.W32(memInfo, LibraryMemSize);
        return 0;
    }

    /// <summary>The camera calls it as (type, 0, initInfo, memInfo) and allocates memInfo+0 bytes for the library; games as
    /// (type, initInfo, memInfo).</summary>
    [Hle] int sceVideoencQueryMemSize(uint codecType, uint a1, uint a2, uint a3) => QueryMemSize(codecType, a1 == 0 && a3 != 0 ? a3 : a2);
    [Hle] int sceVideoencQueryMemSizeInternal(uint codecType, uint a1, uint a2, uint a3) => QueryMemSize(codecType, a1 == 0 && a3 != 0 ? a3 : a2);

    [Hle]
    int sceVideoencTermLibrary(uint codecType)
    {
        lock (_lock)
        {
            _libraries.Remove(codecType);
            if (_libraries.Count == 0)
            {
                foreach (var e in _encoders.Values) e.Codec?.Dispose();
                _encoders.Clear();
            }
        }
        return 0;
    }

    static bool ValidQuery(uint query, out uint w, out uint h)
    {
        w = GuestMemory.R32(query);
        h = GuestMemory.R32(query + 4);
        return w <= MaxWidth && h <= MaxHeight && w * h <= MaxWidth * MaxHeight;
    }

    /// <summary>Encoder memory for the maximum frame size: a few YUV420 frames, 256 KiB aligned (estimate; the game
    /// only allocates it).</summary>
    int QueryEncoderMemSize(uint codecType, uint query, uint info)
    {
        CheckType(codecType);
        if (query == 0 || info == 0) return ERR_INVALID_POINTER;
        if (!ValidQuery(query, out uint w, out uint h)) return ERR_INVALID_PARAM;
        GuestMemory.W32(info, GuestMemory.AlignUp(Math.Max(w * h * 3 / 2 * 3, 1), 0x40000));
        // the camera reads two more sizes after it (a second buffer it allocates in its unmap block, and the stream
        // buffer size it passes on); estimates as well
        GuestMemory.W32(info + 4, 0x40000);
        GuestMemory.W32(info + 8, GuestMemory.AlignUp(Math.Max(w * h, 1), 0x1000));
        return 0;
    }

    [Hle] int sceAvcencQueryEncoderMemSize(uint codecType, uint query, uint info) => QueryEncoderMemSize(codecType, query, info);
    [Hle] int sceAvcencQueryEncoderMemSizeBasic(uint codecType, uint query, uint info) => QueryEncoderMemSize(codecType, query, info);
    [Hle] int sceAvcencQueryEncoderMemSizeInternal(uint codecType, uint query, uint info) => QueryEncoderMemSize(codecType, query, info);

    int CreateEncoder(uint codecType, uint ctrl, uint query)
    {
        CheckType(codecType);
        if (ctrl == 0 || query == 0) return ERR_INVALID_POINTER;
        if (!ValidQuery(query, out uint w, out uint h)) return ERR_INVALID_PARAM;
        lock (_lock)
        {
            uint handle = _nextHandle++;
            _encoders[handle] = new Encoder { Width = w, Height = h };
            GuestMemory.W32(ctrl, handle);
        }
        return 0;
    }

    [Hle] int sceAvcencCreateEncoder(uint codecType, uint ctrl, uint query) => CreateEncoder(codecType, ctrl, query);
    [Hle] int sceAvcencCreateEncoderBasic(uint codecType, uint ctrl, uint query) => CreateEncoder(codecType, ctrl, query);
    [Hle] int sceAvcencCreateEncoderInternal(uint codecType, uint ctrl, uint query) => CreateEncoder(codecType, ctrl, query);

    Encoder? Find(uint ctrl, out int error, out uint handle)
    {
        error = 0;
        handle = 0;
        if (ctrl == 0) { error = ERR_INVALID_POINTER; return null; }
        handle = GuestMemory.R32(ctrl);
        lock (_lock)
            if (_encoders.TryGetValue(handle, out var e)) return e;
        error = ERR_INVALID_PARAM;
        return null;
    }

    [Hle]
    int sceAvcencDeleteEncoder(uint ctrl)
    {
        if (Find(ctrl, out int err, out uint h) is not { } e) return err;
        lock (_lock) _encoders.Remove(h);
        e.Codec?.Dispose();
        return 0;
    }

    /// <summary>Layout not confirmed. What the Camera passes (640x480): +0xC 28 (its IDR interval: it counts 0x1C
    /// pictures between key frames), +0x18/+0x1C/+0x20 width, pitch and height, +0x30 1500000 and +0x34 1800000
    /// (bit rate and its peak). The interval and bit rate are taken when they are plausible, before the first
    /// picture; the first 0x40 bytes are logged.</summary>
    [Hle]
    int sceAvcencSetEncoderParameter(uint ctrl, uint param)
    {
        var e = Find(ctrl, out int err, out _);
        if (e == null) return err;
        if (param == 0) return ERR_INVALID_POINTER;
        Log.Info("sceAvcencSetEncoderParameter: " + Convert.ToHexString(GuestMemory.ReadSpan(param, 0x40)));
        uint gop = GuestMemory.R32(param + 0xC), rate = GuestMemory.R32(param + 0x30);
        if (gop is >= 1 and <= 300) e.Gop = (int)gop;
        if (rate is >= 64_000 and <= 40_000_000) e.BitRate = (int)rate;
        // the picture size (the creation query's second word is not the height: the Camera's is 640x640 there)
        uint w = GuestMemory.R32(param + 0x18), h = GuestMemory.R32(param + 0x20);
        if (!e.Tried && w is > 0 and <= MaxWidth && h is > 0 and <= MaxHeight && (w & 1) == 0 && (h & 1) == 0) (e.Width, e.Height) = (w, h);
        WriteParameterSets(e, ctrl);
        return 0;
    }

    Media.H264Encoder? Codec(Encoder e, uint w, uint h)
    {
        if (!e.Tried)
        {
            e.Tried = true;
            e.Codec = Media.H264Encoder.TryCreate((int)w, (int)h, e.FrameRate, e.BitRate, e.Gop);
        }
        return e.Codec;
    }

    /// <summary>
    /// The encoder's SPS and PPS for the caller, once it is set up (the Camera reads them right after
    /// <see cref="sceAvcencSetEncoderParameter"/> for the MP4's avcC; without them the avcC has empty parameter sets
    /// and the system's video profiler finds no video track: "Could not save the video"). The control block the
    /// Camera gives (0x34 bytes, layout read in NPXS10004): +0 the handle, +4/+8 and +0xC/+0x10 its two encoder
    /// buffers, +0x14/+0x18 a 0x400-byte buffer for the parameter sets; back, +0x1C/+0x20 the SPS (address in that
    /// buffer, size, no start code) and +0x28/+0x2C the PPS.
    /// </summary>
    void WriteParameterSets(Encoder e, uint ctrl)
    {
        uint buf = GuestMemory.R32(ctrl + 0x14), cap = GuestMemory.R32(ctrl + 0x18);
        // a plausible buffer only: other callers' control blocks may hold something else there
        if (cap is < 0x40 or > 0x10000 || !GuestMemory.IsMapped(buf) || !GuestMemory.IsMapped(buf + cap - 1)) return;
        if (Codec(e, e.Width, e.Height)?.ParameterSets() is not var (sps, pps)) return;
        if ((uint)(sps.Length + pps.Length) > cap)
        {
            Log.WarnOnce("avcenc-ps", $"sceAvcenc: parameter sets ({sps.Length + pps.Length} bytes) larger than the caller's buffer ({cap})");
            return;
        }
        sps.CopyTo(GuestMemory.Span(buf, sps.Length));
        pps.CopyTo(GuestMemory.Span(buf + (uint)sps.Length, pps.Length));
        GuestMemory.W32(ctrl + 0x1C, buf);
        GuestMemory.W32(ctrl + 0x20, (uint)sps.Length);
        GuestMemory.W32(ctrl + 0x28, buf + (uint)sps.Length);
        GuestMemory.W32(ctrl + 0x2C, (uint)pps.Length);
    }

    /// <summary>Inferred (ctrl, preset); stored only.</summary>
    [Hle]
    int sceAvcencSetAvailablePreset(uint ctrl, uint preset)
    {
        var e = Find(ctrl, out int err, out _);
        if (e == null) return err;
        e.Preset = preset;
        return 0;
    }

    /// <summary>Picture layout unconfirmed: arguments are validated but nothing is converted or written.</summary>
    [Hle]
    int sceAvcencCsc(uint ctrl, uint dst, uint src)
    {
        if (Find(ctrl, out int err, out _) == null) return err;
        if (dst == 0 || src == 0) return ERR_INVALID_POINTER;
        Log.WarnOnce("avcenc-csc", "sceAvcencCsc: picture layout unconfirmed, conversion skipped");
        return 0;
    }

    /// <summary>
    /// (ctrl, input, output). Input as the Camera fills it (0x30 bytes): +0 size 0x30, +8 pitch, +0xC width, +0x10
    /// height, +0x24 the picture (YUV 4:2:0 planar: the Y plane, then U and V at half pitch right after it, as
    /// SceCamera's YUV420_PLANE frames are), 0 for no picture; +0x2C flags (1). Output: +0 nonzero when a picture was
    /// produced, +4 the number of picture infos and +8 an array of pointers to them. A picture info (0x28 bytes): +0
    /// size, +4/+8/+0xC and +0x10/+0x14/+0x18 the two timestamps as (seconds, numerator, denominator) (the Camera turns
    /// them into its 240 kHz DTS/PTS), +0x1C/+0x20 the stream buffer the caller gives and, back, the bytes written, +0x24
    /// the number of NAL units and +0x25 the picture type (4 = IDR). The NAL units go to the stream buffer in Annex B
    /// with 4-byte start codes: the Camera overwrites each start code with the unit's big-endian length (MP4 samples).
    /// </summary>
    [Hle]
    int sceAvcencEncode(uint ctrl, uint input, uint output)
    {
        var e = Find(ctrl, out int err, out _);
        if (e == null) return err;
        if (input == 0) return ERR_INVALID_POINTER;
        e.Frames++;
        e.Stopped = false;
        uint y = GuestMemory.R32(input + 0x24);
        if (y == 0 || output == 0) { NoPicture(output); return 0; }
        uint pitch = GuestMemory.R32(input + 8), w = GuestMemory.R32(input + 0xC), h = GuestMemory.R32(input + 0x10);
        if (w == 0 || h == 0 || w > MaxWidth || h > MaxHeight) (w, h) = (e.Width, e.Height);
        if (pitch < w) pitch = w;
        if (Codec(e, w, h) is not { } codec || codec.Width != w || codec.Height != h) { NoPicture(output); return 0; }
        uint ySize = pitch * h, cPitch = pitch / 2, cSize = cPitch * (h / 2);
        var units = codec.Encode(GuestMemory.ReadSpan(y, (int)ySize), (int)pitch,
            GuestMemory.ReadSpan(y + ySize, (int)cSize), GuestMemory.ReadSpan(y + ySize + cSize, (int)cSize), (int)cPitch);
        WritePicture(e, output, units);
        return 0;
    }

    /// <summary>Describes the encoded access units (normally one) in the output and the caller's stream buffer.</summary>
    static void WritePicture(Encoder e, uint output, List<(byte[] Data, bool Key)> units)
    {
        e.Nals.Clear();
        uint list = output == 0 ? 0 : GuestMemory.R32(output + 8);
        uint info = units.Count == 0 || list == 0 || GuestMemory.R32(output + 4) == 0 ? 0 : GuestMemory.R32(list);
        if (info == 0 || GuestMemory.R32(info) < 0x26) { NoPicture(output); return; }
        uint buf = GuestMemory.R32(info + 0x1C), cap = GuestMemory.R32(info + 0x20), pos = 0;
        bool key = false;
        foreach (var (data, k) in units)
        {
            key |= k;
            foreach (var (off, len) in Media.H264Encoder.SplitNals(data))
            {
                if (pos + 4 + (uint)len > cap)
                {
                    Log.WarnOnce("avcenc-full", $"sceAvcencEncode: picture larger than the stream buffer ({cap} bytes), NAL units dropped");
                    break;
                }
                GuestMemory.W32(buf + pos, 0x01000000); // 00 00 00 01
                data.AsSpan(off, len).CopyTo(GuestMemory.Span(buf + pos + 4, len));
                e.Nals.Add((buf + pos + 4, (uint)len, (byte)(data[off] & 0x1F)));
                pos += 4 + (uint)len;
            }
        }
        if (e.Nals.Count == 0) { NoPicture(output); return; }
        long n = e.Pictures++;
        uint fps = (uint)Math.Max(e.FrameRate, 1);
        for (uint t = 4; t <= 0x10; t += 0xC) // decoding and presentation time are the same (no B-frames)
        {
            GuestMemory.W32(info + t, (uint)(n / fps));
            GuestMemory.W32(info + t + 4, (uint)(n % fps));
            GuestMemory.W32(info + t + 8, fps);
        }
        GuestMemory.W32(info + 0x20, pos);
        GuestMemory.Write(info + 0x24, (byte)Math.Min(e.Nals.Count, 255));
        GuestMemory.Write(info + 0x25, (byte)(key ? 4 : 1));
        GuestMemory.W32(output, 1);
    }

    [Hle] int sceAvcencEncodeFlush(uint ctrl) => Find(ctrl, out int err, out _) == null ? err : 0;

    /// <summary>(ctrl, output): the end of the stream; what the encoder still holds comes out as in Encode.</summary>
    [Hle]
    int sceAvcencEncodeStop(uint ctrl, uint output)
    {
        var e = Find(ctrl, out int err, out _);
        if (e == null) return err;
        e.Stopped = true;
        if (e.Codec != null && output != 0) WritePicture(e, output, e.Codec.Flush());
        else NoPicture(output);
        return 0;
    }

    /// <summary>No picture: output +0 = 0 (left untouched, the Camera read garbage NAL entries and jumped through a null
    /// function pointer).</summary>
    static void NoPicture(uint output)
    {
        if (output != 0) GuestMemory.W32(output, 0);
    }

    /// <summary>(ctrl, index, SceAvcencNalUnit *nal): +0 the unit's address in the stream buffer (after its start code),
    /// +4 its size, +8 its nal_unit_type (byte; the Camera marks a picture with a type 5 unit as a key frame).</summary>
    [Hle]
    int sceAvcencGetNalUnit(uint ctrl, uint index, uint nal)
    {
        var e = Find(ctrl, out int err, out _);
        if (e == null) return err;
        if (nal == 0) return ERR_INVALID_POINTER;
        if (index >= (uint)e.Nals.Count) return ERR_INVALID_PARAM;
        var (addr, size, type) = e.Nals[(int)index];
        GuestMemory.W32(nal, addr);
        GuestMemory.W32(nal + 4, size);
        GuestMemory.Write(nal + 8, type);
        return 0;
    }

    internal long FramesOf(uint handle) { lock (_lock) return _encoders.TryGetValue(handle, out var e) ? e.Frames : -1; }
}
