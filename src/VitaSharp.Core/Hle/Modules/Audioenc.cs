using VitaSharp.Core.Memory;
using VitaSharp.Core.Util;

namespace VitaSharp.Core.Hle.Modules;

/// <summary>
/// SceAudioencUser: hardware audio encoder; the only codec is CELP (MPEG-4 CELP NB, 8 kHz). There is no CELP encoder
/// (FFmpeg has none and nothing in the emulator could decode it), so frames of the correct size are produced with a
/// zero payload: the protocol (handles, sizes, consumed/produced byte counts) is what games depend on.
/// SceAudioencCtrl (0x2C): size, handle, pInputPcm, inputPcmSize, maxPcmSize, pOutputEs, outputEsSize, maxEsSize,
/// wordLength, pInfo, pOptInfo. SceAudioencInfoCelp (0x10): size, excitationMode, samplingRate, bitRate.
/// </summary>
public sealed class Audioenc : HleModule
{
    internal const int ERR_INVALID_TYPE = unchecked((int)0x80860001), ERR_INVALID_INIT_PARAM = unchecked((int)0x80860002),
        ERR_ALREADY_INITIALIZED = unchecked((int)0x80860003), ERR_NOT_INITIALIZED = unchecked((int)0x80860005),
        ERR_A_HANDLE_IN_USE = unchecked((int)0x80860006), ERR_ALL_HANDLES_IN_USE = unchecked((int)0x80860007),
        ERR_INVALID_PTR = unchecked((int)0x80860008), ERR_INVALID_HANDLE = unchecked((int)0x80860009),
        ERR_NOT_HANDLE_IN_USE = unchecked((int)0x8086000A), ERR_INVALID_WORD_LENGTH = unchecked((int)0x8086000C),
        ERR_INVALID_SIZE = unchecked((int)0x8086000D), ERR_CELP_INVALID_CONFIG = unchecked((int)0x80861001);
    internal const uint TYPE_CELP = 0x2006;
    const uint CtrlSize = 0x2C, InfoSize = 0x10, OptInfoSize = 0x2C, InitParamSize = 8, MaxStreams = 1;
    const uint ContextSize = 0x10000;

    /// <summary>CELP bitrate → (samples per frame at 8 kHz, bytes per frame). 3850/4650/5700/7300/12200 are confirmed
    /// by psp2/voice.h; the others follow the MPEG-4 CELP NB frame lengths (10 ms ≥ 9900 bps, else 20 ms) and are to
    /// be confirmed on hardware.</summary>
    internal static (int Samples, int Bytes)? Frame(uint bitRate) => bitRate switch
    {
        3850 => (320, 20), 4650 => (320, 24), 5700 => (160, 15), 6600 => (160, 17), 7300 => (160, 19),
        8700 => (160, 22), 9900 => (80, 13), 10700 => (80, 14), 11800 => (80, 15), 12200 => (80, 16), _ => null,
    };

    sealed class Encoder
    {
        public uint BitRate;
        public int Samples, Bytes;
        public bool Library; // counts against sceAudioencInitLibrary's totalStreams
        public bool External, Resident;
        public uint Context;
    }

    readonly object _lock = new();
    uint _totalStreams; // 0 = library not initialized
    readonly Dictionary<uint, Encoder> _encoders = new();
    uint _nextHandle = 1;

    /// <summary>SceAudioencInitParam (8): celp = { size (=8), totalStreams }.</summary>
    [Hle]
    int sceAudioencInitLibrary(uint codecType, uint initParam)
    {
        if (codecType != TYPE_CELP) return ERR_INVALID_TYPE;
        if (initParam == 0) return ERR_INVALID_PTR;
        uint size = GuestMemory.R32(initParam), streams = GuestMemory.R32(initParam + 4);
        if (size != InitParamSize || streams is 0 or > MaxStreams) return ERR_INVALID_INIT_PARAM;
        lock (_lock)
        {
            if (_totalStreams != 0) return ERR_ALREADY_INITIALIZED;
            _totalStreams = streams;
        }
        return 0;
    }

    [Hle]
    int sceAudioencTermLibrary(uint codecType)
    {
        if (codecType != TYPE_CELP) return ERR_INVALID_TYPE;
        lock (_lock)
        {
            if (_totalStreams == 0) return ERR_NOT_INITIALIZED;
            if (_encoders.Values.Any(e => e.Library)) return ERR_A_HANDLE_IN_USE;
            _totalStreams = 0;
        }
        return 0;
    }

    [Hle] int sceAudioencCreateEncoder(uint ctrl, uint codecType) => Create(ctrl, codecType, library: true, resident: false, context: 0);
    [Hle] int sceAudioencCreateEncoderResident(uint ctrl, uint codecType) => Create(ctrl, codecType, library: false, resident: true, context: 0); // system-reserved memory: InitLibrary not required (to confirm)

    /// <summary>Inferred from sceAudiodecCreateDecoderExternal: the app supplies the context memory, no InitLibrary.</summary>
    [Hle]
    int sceAudioencCreateEncoderExternal(uint ctrl, uint codecType, uint vaContext, uint contextSize)
    {
        if (vaContext == 0) return ERR_INVALID_PTR;
        if (contextSize < ContextSize) return ERR_INVALID_SIZE;
        return Create(ctrl, codecType, library: false, resident: false, context: vaContext);
    }

    /// <summary>Bytes of context an External encoder needs (value to confirm; same as sceAudiodecGetContextSize).</summary>
    [Hle]
    int sceAudioencGetContextSize(uint ctrl, uint codecType)
    {
        if (codecType != TYPE_CELP) return ERR_INVALID_TYPE;
        return (int)ContextSize;
    }

    int Create(uint ctrl, uint codecType, bool library, bool resident, uint context)
    {
        if (ctrl == 0) return ERR_INVALID_PTR;
        if (GuestMemory.R32(ctrl) != CtrlSize) return ERR_INVALID_SIZE;
        if (codecType != TYPE_CELP) return ERR_INVALID_TYPE;
        if (GuestMemory.R32(ctrl + 0x20) != 16) return ERR_INVALID_WORD_LENGTH;
        uint info = GuestMemory.R32(ctrl + 0x24);
        if (info == 0) return ERR_INVALID_PTR;
        if (GuestMemory.R32(info) != InfoSize) return ERR_INVALID_SIZE;
        uint mode = GuestMemory.R32(info + 4), rate = GuestMemory.R32(info + 8), bitRate = GuestMemory.R32(info + 12);
        if (mode != 0 || rate != 8000 || Frame(bitRate) is not { } f) return ERR_CELP_INVALID_CONFIG;
        lock (_lock)
        {
            if (library)
            {
                if (_totalStreams == 0) return ERR_NOT_INITIALIZED;
                if (_encoders.Values.Count(e => e.Library) >= _totalStreams) return ERR_ALL_HANDLES_IN_USE;
            }
            uint h = _nextHandle++;
            _encoders[h] = new Encoder
            {
                BitRate = bitRate, Samples = f.Samples, Bytes = f.Bytes,
                Library = library, Resident = resident, External = context != 0, Context = context,
            };
            GuestMemory.W32(ctrl + 4, h);
        }
        GuestMemory.W32(ctrl + 0x10, (uint)(f.Samples * 2)); // maxPcmSize
        GuestMemory.W32(ctrl + 0x1C, (uint)f.Bytes);         // maxEsSize
        Log.WarnOnce("audioenc-celp", "sceAudioenc: no CELP encoder, frames are emitted with a silent (zero) payload");
        return 0;
    }

    Encoder? Find(uint ctrl, out int error, out uint handle)
    {
        error = 0;
        handle = 0;
        if (ctrl == 0) { error = ERR_INVALID_PTR; return null; }
        if (GuestMemory.R32(ctrl) != CtrlSize) { error = ERR_INVALID_SIZE; return null; }
        handle = GuestMemory.R32(ctrl + 4);
        if (handle == 0) { error = ERR_INVALID_HANDLE; return null; }
        lock (_lock)
            if (_encoders.TryGetValue(handle, out var e)) return e;
        error = ERR_NOT_HANDLE_IN_USE;
        return null;
    }

    /// <summary>Any variant deletes any encoder kind (whether the hardware checks the kind: to confirm).</summary>
    int Delete(uint ctrl, uint pvaContext)
    {
        var e = Find(ctrl, out int err, out uint h);
        if (e == null) return err;
        lock (_lock) _encoders.Remove(h);
        if (pvaContext != 0) GuestMemory.W32(pvaContext, e.Context);
        return 0;
    }

    [Hle] int sceAudioencDeleteEncoder(uint ctrl) => Delete(ctrl, 0);
    [Hle] int sceAudioencDeleteEncoderResident(uint ctrl) => Delete(ctrl, 0);
    /// <summary>Writes the context address given at creation to *pvaContext so the app can free it.</summary>
    [Hle] int sceAudioencDeleteEncoderExternal(uint ctrl, uint pvaContext) => Delete(ctrl, pvaContext);

    [Hle] int sceAudioencEncode(uint ctrl) => EncodeFrames(ctrl, 1);
    [Hle] int sceAudioencEncodeNFrames(uint ctrl, uint frames) => EncodeFrames(ctrl, Math.Max(frames, 1));

    int EncodeFrames(uint ctrl, uint frames)
    {
        var e = Find(ctrl, out int err, out _);
        if (e == null) return err;
        uint pcm = GuestMemory.R32(ctrl + 8), es = GuestMemory.R32(ctrl + 0x14);
        if (pcm == 0 || es == 0) return ERR_INVALID_PTR;
        uint total = (uint)e.Bytes * frames;
        GuestMemory.Fill(es, 0, total);
        GuestMemory.W32(ctrl + 0x0C, (uint)e.Samples * 2 * frames); // inputPcmSize
        GuestMemory.W32(ctrl + 0x18, total);                         // outputEsSize
        return 0;
    }

    [Hle]
    int sceAudioencClearContext(uint ctrl) => Find(ctrl, out int err, out _) == null ? err : 0;

    /// <summary>SceAudioencOptInfoCelp (0x2C): size, header[32], headerSize, encoderVersion. The real CELP header and
    /// encoder version are unknown: header zeroed, headerSize 0, version 0.</summary>
    [Hle]
    int sceAudioencGetOptInfo(uint ctrl)
    {
        var e = Find(ctrl, out int err, out _);
        if (e == null) return err;
        uint opt = GuestMemory.R32(ctrl + 0x28);
        if (opt == 0) return ERR_INVALID_PTR;
        if (GuestMemory.R32(opt) != OptInfoSize) return ERR_INVALID_SIZE;
        GuestMemory.Fill(opt + 4, 0, OptInfoSize - 4);
        return 0;
    }

    [Hle]
    int sceAudioencGetInternalError(uint ctrl, uint pInternalError)
    {
        var e = Find(ctrl, out int err, out _);
        if (e == null) return err;
        if (pInternalError == 0) return ERR_INVALID_PTR;
        GuestMemory.W32(pInternalError, 0);
        return 0;
    }
}
