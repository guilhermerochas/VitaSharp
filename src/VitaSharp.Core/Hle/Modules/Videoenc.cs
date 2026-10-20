using VitaSharp.Core.Memory;
using VitaSharp.Core.Util;

namespace VitaSharp.Core.Hle.Modules;

/// <summary>
/// SceVideoencUser / SceAvcenc: hardware H.264 encoder (used for video recording, usually through SceLibMp4Recorder).
/// There is no vitasdk header and no Vita3K module: prototypes are inferred from the SceVideodec/SceAvcdec pair
/// (Videodec.cs). Functional stub: libraries and encoder handles are tracked and arguments validated, frames are
/// accepted and dropped, and no NAL units are ever produced (the output only goes to a file or the network, never
/// back to the game). Error codes follow SceVideodec (INVALID_PARAM / INVALID_POINTER) until confirmed.
/// Only words whose layout is assumed by analogy with SceAvcdec are touched: the handle at ctrl+0, the memory size at
/// memInfo+0 / encoderInfo+0, width/height at query+0/+4. Nothing is written through picture/NAL structs.
/// </summary>
public sealed class Videoenc : HleModule
{
    internal const int ERR_INVALID_PARAM = unchecked((int)0x80620002), ERR_INVALID_POINTER = unchecked((int)0x80620009);
    /// <summary>Probable SCE_VIDEOENC_TYPE_HW_AVCENC (by analogy with TYPE_HW_AVCDEC = 0x1001); any type is accepted.</summary>
    internal const uint TYPE_HW_AVCENC_GUESS = 0x2001;
    const uint LibraryMemSize = 0x40000, MaxWidth = 1920, MaxHeight = 1088;

    sealed class Encoder
    {
        public uint Width, Height, Preset;
        public long Frames;
        public bool Stopped;
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
        Log.WarnOnce("videoenc-stub", "sceVideoenc/sceAvcenc: H.264 encoding is not emulated, frames are dropped");
        lock (_lock) _libraries.Add(codecType);
        return 0;
    }

    [Hle] int sceVideoencInitLibrary(uint codecType, uint initInfo) => InitLibrary(codecType);
    [Hle] int sceVideoencInitLibraryInternal(uint codecType, uint initInfo) => InitLibrary(codecType);
    [Hle] int sceVideoencInitLibraryWithUnmapMem(uint codecType, uint initInfo, uint memInfo) => memInfo == 0 ? ERR_INVALID_POINTER : InitLibrary(codecType);
    [Hle] int sceVideoencInitLibraryWithUnmapMemInternal(uint codecType, uint initInfo, uint memInfo) => memInfo == 0 ? ERR_INVALID_POINTER : InitLibrary(codecType);

    /// <summary>Writes the library memory size at memInfo+0 (layout as in sceVideodecQueryMemSize).</summary>
    int QueryMemSize(uint codecType, uint memInfo)
    {
        CheckType(codecType);
        if (memInfo == 0) return ERR_INVALID_POINTER;
        GuestMemory.W32(memInfo, LibraryMemSize);
        return 0;
    }

    [Hle] int sceVideoencQueryMemSize(uint codecType, uint initInfo, uint memInfo) => QueryMemSize(codecType, memInfo);
    [Hle] int sceVideoencQueryMemSizeInternal(uint codecType, uint initInfo, uint memInfo) => QueryMemSize(codecType, memInfo);

    [Hle]
    int sceVideoencTermLibrary(uint codecType)
    {
        lock (_lock)
        {
            _libraries.Remove(codecType);
            if (_libraries.Count == 0) _encoders.Clear();
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
        if (Find(ctrl, out int err, out uint h) == null) return err;
        lock (_lock) _encoders.Remove(h);
        return 0;
    }

    /// <summary>Parameter layout unknown: the first 0x40 bytes are logged once.</summary>
    [Hle]
    int sceAvcencSetEncoderParameter(uint ctrl, uint param)
    {
        if (Find(ctrl, out int err, out _) == null) return err;
        if (param == 0) return ERR_INVALID_POINTER;
        Log.DebugOnce("avcenc-param", "sceAvcencSetEncoderParameter: " + Convert.ToHexString(GuestMemory.Span(param, 0x40)));
        return 0;
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

    /// <summary>The frame is counted and dropped (no H.264 encoder).</summary>
    [Hle]
    int sceAvcencEncode(uint ctrl, uint input, uint output)
    {
        var e = Find(ctrl, out int err, out _);
        if (e == null) return err;
        if (input == 0) return ERR_INVALID_POINTER;
        e.Frames++;
        e.Stopped = false;
        return 0;
    }

    [Hle] int sceAvcencEncodeFlush(uint ctrl) => Find(ctrl, out int err, out _) == null ? err : 0;

    [Hle]
    int sceAvcencEncodeStop(uint ctrl, uint output)
    {
        var e = Find(ctrl, out int err, out _);
        if (e == null) return err;
        e.Stopped = true;
        return 0;
    }

    /// <summary>The NAL queue is always empty. The "no data" code and the NAL struct layout are unconfirmed, so the
    /// struct is left untouched and 0 is returned.</summary>
    [Hle]
    int sceAvcencGetNalUnit(uint ctrl, uint nal)
    {
        if (Find(ctrl, out int err, out _) == null) return err;
        if (nal == 0) return ERR_INVALID_POINTER;
        Log.WarnOnce("avcenc-nal", "sceAvcencGetNalUnit: no NAL units are produced (encoder not emulated)");
        return 0;
    }

    internal long FramesOf(uint handle) { lock (_lock) return _encoders.TryGetValue(handle, out var e) ? e.Frames : -1; }
}
