using VitaSharp.Core.Media;
using VitaSharp.Core.Memory;

namespace VitaSharp.Core.Hle.Modules;

// The rest of SceVideodecUser (avcodec_us.suprx, FW 3.74) and the SceAvcodec syscalls behind it: every user entry
// forwards its arguments unchanged to the syscall of the same name with a leading underscore, which copies the
// control block (0xC), the access unit (0x18; 0x30 for the NAL-unit forms), the picture array (0xC) or a picture
// (0x64 AVC, 0x7C its Internal form, 0x5C MPEG-4) in and out. The Internal/Nongameapp forms are the system
// applications' entries to the same decoder (they only differ in the memory the kernel takes the decoder from).
// MPEG-4 part 2 (M4vdec, the Videos application's .mp4 files) decodes through FFmpeg like H.264.
public sealed partial class Videodec
{
    const uint TYPE_HW_M4VDEC = 0x2001;

    readonly Dictionary<uint, (uint Callback, uint Arg)> _callbacks = new();
    readonly Dictionary<int, int> _config = new();

    // ---- AVC: split decode (unit in, pictures out separately) and the system forms
    /// <summary>(SceAvcdecCtrl *ctrl, const SceAvcdecAu *au, int *needsPicture): feeds one access unit; the pictures
    /// come out with sceAvcdecDecodeGetPicture. The third word tells whether a picture is ready (1) or not (0).</summary>
    [Hle("sceAvcdecDecodeAuInternal")]
    [Hle("_sceAvcdecDecodeAuInternal")]
    [Hle("_sceAvcdecDecodeAuNongameapp")]
    int DecodeAu(uint ctrl, uint au, uint ready)
    {
        var d = Find(ctrl, out int err);
        if (d == null) return err;
        if (au == 0) return ERR_INVALID_POINTER;
        Feed(d, au);
        if (ready != 0) GuestMemory.W32(ready, d.Codec != null ? 1u : 0u);
        return 0;
    }

    void Feed(Decoder d, uint au)
    {
        long pts = ((long)GuestMemory.R32(au) << 32) | GuestMemory.R32(au + 4);
        uint es = GuestMemory.R32(au + 16), size = GuestMemory.R32(au + 20);
        if (d.Codec == null || es == 0 || size == 0) return;
        d.Codec.Send(GuestMemory.Span(es, (int)size), pts);
        if (d.AuPts != long.MinValue && pts < d.AuPts) d.AuReordered = true;
        d.AuPts = pts;
        d.Calls++;
    }

    /// <summary>(SceAvcdecCtrl *ctrl, SceAvcdecArrayPicture *array, int *more): the pictures decoded so far
    /// (sceAvcdecDecodeAu's), the third word = pictures still waiting.</summary>
    [Hle("sceAvcdecDecodeGetPictureInternal")]
    [Hle("_sceAvcdecDecodeGetPictureInternal")]
    [Hle("_sceAvcdecDecodeGetPictureNongameapp")]
    int DecodeGetPicture(uint ctrl, uint array, uint more)
    {
        var d = Find(ctrl, out int err);
        if (d == null) return err;
        if (array == 0) return ERR_INVALID_POINTER;
        int r = Output(d, array);
        if (more != 0) GuestMemory.W32(more, (uint)d.Ready.Count);
        return r;
    }

    /// <summary>(ctrl, array, workPicture, more): the same with a work picture the hardware writes the reference
    /// frame into (not needed by the software decoder).</summary>
    [Hle("sceAvcdecDecodeGetPictureWithWorkPictureInternal")]
    [Hle("_sceAvcdecDecodeGetPictureWithWorkPictureInternal")]
    int DecodeGetPictureWithWork(uint ctrl, uint array, uint work, uint more) => DecodeGetPicture(ctrl, array, more);

    /// <summary>(ctrl, const SceAvcdecNalAu *au 0x30, ...): an access unit given as NAL units (Remote Play / PS4
    /// Link streams). The block starts like SceAvcdecAu (pts, dts, es pointer and size): the NAL units of the unit,
    /// Annex B or 4-byte length prefixed (converted to start codes).</summary>
    [Hle("_sceAvcdecDecodeNalAu")]
    int DecodeNalAu(uint ctrl, uint au, uint array)
    {
        var d = Find(ctrl, out int err);
        if (d == null) return err;
        if (au == 0 || array == 0) return ERR_INVALID_POINTER;
        FeedNal(d, au);
        return Output(d, array, repeatIfLate: true);
    }

    [Hle("_sceAvcdecDecodeNalAuWithWorkPicture")]
    int DecodeNalAuWithWork(uint ctrl, uint au, uint work, uint array) => DecodeNalAu(ctrl, au, array);

    [Hle("sceAvcdecDecodeAuNalAuInternal")]
    [Hle("_sceAvcdecDecodeAuNalAuInternal")]
    [Hle("_sceAvcdecDecodeAuNalAuNongameapp")]
    int DecodeAuNalAu(uint ctrl, uint au, uint ready)
    {
        var d = Find(ctrl, out int err);
        if (d == null) return err;
        if (au == 0) return ERR_INVALID_POINTER;
        FeedNal(d, au);
        if (ready != 0) GuestMemory.W32(ready, d.Codec != null ? 1u : 0u);
        return 0;
    }

    void FeedNal(Decoder d, uint au)
    {
        long pts = ((long)GuestMemory.R32(au) << 32) | GuestMemory.R32(au + 4);
        uint es = GuestMemory.R32(au + 16), size = GuestMemory.R32(au + 20);
        if (d.Codec == null || es == 0 || size == 0) return;
        var data = GuestMemory.ReadSpan(es, (int)size);
        d.Codec.Send(ToAnnexB(data), pts);
        d.AuPts = pts;
        d.Calls++;
    }

    /// <summary>NAL units with 4-byte big-endian length prefixes become Annex B (00 00 00 01 start codes); data that
    /// already starts with a start code is passed as is.</summary>
    internal static byte[] ToAnnexB(ReadOnlySpan<byte> data)
    {
        if (data.Length >= 4 && data[0] == 0 && data[1] == 0 && (data[2] == 1 || (data[2] == 0 && data[3] == 1))) return data.ToArray();
        var outp = new List<byte>(data.Length + 16);
        int i = 0;
        while (i + 4 <= data.Length)
        {
            int n = data[i] << 24 | data[i + 1] << 16 | data[i + 2] << 8 | data[i + 3];
            i += 4;
            if (n <= 0 || i + n > data.Length) break;
            outp.AddRange([0, 0, 0, 1]);
            outp.AddRange(data.Slice(i, n).ToArray());
            i += n;
        }
        return outp.Count > 0 ? outp.ToArray() : data.ToArray();
    }

    /// <summary>(ctrl, const SceAvcdecAu *au, workPicture, array): sceAvcdecDecode with a work picture.</summary>
    [Hle("_sceAvcdecDecodeWithWorkPicture")]
    int DecodeWithWork(uint ctrl, uint au, uint work, uint array) => sceAvcdecDecode(ctrl, au, array);

    /// <summary>(ctrl, workPicture, array): sceAvcdecDecodeStop with a work picture.</summary>
    [Hle("sceAvcdecDecodeStopWithWorkPicture")]
    [Hle("_sceAvcdecDecodeStopWithWorkPicture")]
    int DecodeStopWithWork(uint ctrl, uint work, uint array) => sceAvcdecDecodeStop(ctrl, array);

    // ---- AVC decoder modes: kept per decoder (the software decoder needs none of them)
    readonly Dictionary<uint, int> _modes = new();

    int SetMode(uint ctrl, int kind, int mode)
    {
        var d = Find(ctrl, out int err);
        if (d == null) return err;
        lock (_modes) _modes[GuestMemory.R32(ctrl) * 16 + (uint)kind] = mode;
        return 0;
    }

    /// <summary>(ctrl, int mode): decode only I/IP frames (fast forward) and similar modes.</summary>
    [Hle("sceAvcdecSetDecodeMode")]
    [Hle("_sceAvcdecSetDecodeMode")]
    [Hle("sceAvcdecSetDecodeModeInternal")]
    [Hle("_sceAvcdecSetDecodeModeInternal")]
    int SetDecodeMode(uint ctrl, int mode) => SetMode(ctrl, 0, mode);

    /// <summary>(ctrl, int mode): an interlaced (field) stream.</summary>
    [Hle("sceAvcdecSetInterlacedStreamMode")]
    [Hle("_sceAvcdecSetInterlacedStreamMode")]
    int SetInterlacedStreamMode(uint ctrl, int mode) => SetMode(ctrl, 1, mode);

    /// <summary>(ctrl, int mode): start decoding at recovery point SEIs (streams without IDR frames).</summary>
    [Hle("_sceAvcdecSetRecoveryPointSEIMode")]
    int SetRecoveryPointSeiMode(uint ctrl, int mode) => SetMode(ctrl, 2, mode);

    /// <summary>(ctrl, int mode): output each picture as soon as it is decoded (no reordering delay).</summary>
    [Hle("_sceAvcdecSetLowDelayModeNongameapp")]
    int SetLowDelayMode(uint ctrl, int mode) => SetMode(ctrl, 3, mode);

    /// <summary>(ctrl, int mode): trick play (skip non-reference frames) for the video player.</summary>
    [Hle("sceAvcdecDecodeSetTrickModeNongameapp")]
    [Hle("_sceAvcdecDecodeSetTrickModeNongameapp")]
    int SetTrickMode(uint ctrl, int mode) => SetMode(ctrl, 4, mode);

    /// <summary>(ctrl, SceSize size): memory for the user-data SEIs of one field.</summary>
    [Hle("_sceAvcdecDecodeSetUserDataSei1FieldMemSizeNongameapp")]
    int SetUserDataSeiMemSize(uint ctrl, int size) => SetMode(ctrl, 5, size);

    /// <summary>(ctrl, SceAvcdecSeiPictureTiming *timing 0x4C): the picture timing SEI of the last picture. The
    /// software decoder does not keep SEIs: an empty block (no timing present).</summary>
    [Hle("sceAvcdecGetSeiPictureTimingInternal")]
    [Hle("_sceAvcdecGetSeiPictureTimingInternal")]
    int GetSeiPictureTiming(uint ctrl, uint timing)
    {
        var d = Find(ctrl, out int err);
        if (d == null) return err;
        if (timing == 0) return ERR_INVALID_POINTER;
        GuestMemory.Fill(timing, 0, 0x4C);
        return 0;
    }

    /// <summary>(ctrl, SceAvcdecSeiUserData *data 0x84, ...): the user-data SEIs (closed captions): none kept.</summary>
    [Hle("_sceAvcdecGetSeiUserDataNongameapp")]
    int GetSeiUserData(uint ctrl, uint data, uint info)
    {
        var d = Find(ctrl, out int err);
        if (d == null) return err;
        if (data == 0) return ERR_INVALID_POINTER;
        GuestMemory.W32(data, 0);
        return 0;
    }

    // ---- callbacks (the player is told when a picture is ready); the decode calls here answer at once
    [Hle("sceAvcdecRegisterCallbackInternal")]
    [Hle("_sceAvcdecRegisterCallbackInternal")]
    [Hle("_sceAvcdecRegisterCallbackNongameapp")]
    int RegisterCallback(uint ctrl, uint callback, uint arg)
    {
        var d = Find(ctrl, out int err);
        if (d == null) return err;
        lock (_callbacks) _callbacks[GuestMemory.R32(ctrl)] = (callback, arg);
        return 0;
    }

    [Hle("sceAvcdecUnregisterCallbackInternal")]
    [Hle("_sceAvcdecUnregisterCallbackInternal")]
    [Hle("_sceAvcdecUnregisterCallbackNongameapp")]
    [Hle("_sceAvcdecUnregisterCallbackWithCbidInternal")]
    [Hle("_sceAvcdecUnregisterCallbackWithCbidNongameapp")]
    int UnregisterCallback(uint ctrl, uint cbid)
    {
        var d = Find(ctrl, out int err);
        if (d == null) return err;
        lock (_callbacks) return _callbacks.Remove(GuestMemory.R32(ctrl)) ? 0 : ERR_INVALID_PARAM;
    }

    /// <summary>(int key, int value): a library-wide decoder setting, kept.</summary>
    [Hle("sceVideodecSetConfig")]
    [Hle("_sceVideodecSetConfig")]
    [Hle("sceVideodecSetConfigInternal")]
    [Hle("_sceVideodecSetConfigInternal")]
    int SetConfig(int key, int value)
    {
        lock (_config) _config[key] = value;
        return 0;
    }

    /// <summary>(SceVideodecQueryInstance *q 0x1C): how many more decoder instances of a type fit: the software
    /// decoder has no fixed pool: the hardware's 2 for each type (+0x18), minus those created.</summary>
    [Hle("_sceVideodecQueryInstanceNongameapp")]
    int QueryInstance(uint query)
    {
        if (query == 0) return ERR_INVALID_POINTER;
        int used;
        lock (_decoders) used = _decoders.Count;
        GuestMemory.W32(query + 0x18, (uint)Math.Max(0, 2 - used));
        return 0;
    }

    // ---- colour-space conversion of a decoded picture
    /// <summary>(SceAvcdecPicture *dst, const SceAvcdecPicture *src): converts a YUV420 picture (planar or packed)
    /// into the destination's RGBA/BGRA buffer (the destination frame's pixel type, pitch and colour-space byte).</summary>
    [Hle("_sceAvcdecCsc")]
    [Hle("sceAvcdecCscInternal")]
    [Hle("_sceAvcdecCscInternal")]
    [Hle("_sceM4vdecCsc")]
    int Csc(uint dst, uint src)
    {
        if (dst == 0 || src == 0) return ERR_INVALID_POINTER;
        uint sf = src + 4, df = dst + 4;
        uint sType = GuestMemory.R32(sf) & 0x38, sPitch = GuestMemory.R32(sf + 4), sBufH = GuestMemory.R32(sf + 12);
        uint w = GuestMemory.R32(sf + 16), h = GuestMemory.R32(sf + 20), sPtr = GuestMemory.R32(sf + 0x38);
        uint dType = GuestMemory.R32(df) & 0x38, dPitch = GuestMemory.R32(df + 4), dPtr = GuestMemory.R32(df + 0x38);
        if (sType is not (PIXEL_YUV420_RASTER or PIXEL_YUV420_PACKED_RASTER) || dType is not (PIXEL_RGBA8888 or PIXEL_BGRA8888)) return ERR_INVALID_PARAM;
        if (sPtr == 0 || dPtr == 0) return ERR_INVALID_POINTER;
        if (sPitch == 0) sPitch = w;
        if (dPitch == 0) dPitch = w;
        if (sBufH == 0) sBufH = h;
        int cw = (int)(w + 1) / 2, ch = (int)(h + 1) / 2;
        var p = new DecodedPicture { Width = (int)w, Height = (int)h, Y = new byte[w * h], U = new byte[cw * ch], V = new byte[cw * ch] };
        for (uint y = 0; y < h; y++) GuestMemory.ReadSpan(sPtr + y * sPitch, (int)w).CopyTo(p.Y.AsSpan((int)(y * w)));
        uint cOff = sPitch * sBufH;
        for (int y = 0; y < ch; y++)
        {
            if (sType == PIXEL_YUV420_RASTER)
            {
                uint vOff = cOff + sPitch / 2 * (sBufH / 2);
                GuestMemory.ReadSpan(sPtr + cOff + (uint)y * (sPitch / 2), cw).CopyTo(p.U.AsSpan(y * cw));
                GuestMemory.ReadSpan(sPtr + vOff + (uint)y * (sPitch / 2), cw).CopyTo(p.V.AsSpan(y * cw));
            }
            else
            {
                var row = GuestMemory.ReadSpan(sPtr + cOff + (uint)y * sPitch, cw * 2);
                for (int x = 0; x < cw; x++) { p.V[y * cw + x] = row[2 * x]; p.U[y * cw + x] = row[2 * x + 1]; }
            }
        }
        byte alpha = GuestMemory.Read<byte>(df + 0x28), csc = GuestMemory.Read<byte>(df + 0x29);
        ToRgb(p, dPtr, dPitch, (int)Math.Min(w, dPitch), (int)h, dType == PIXEL_BGRA8888, alpha, csc is 2 or 4, csc is 3 or 4);
        GuestMemory.W32(df + 16, w);
        GuestMemory.W32(df + 20, h);
        return 0;
    }

    // ---- MPEG-4 part 2 (M4vdec): the same control/picture blocks as AVC
    [Hle("sceM4vdecQueryDecoderMemSize")]
    [Hle("_sceM4vdecQueryDecoderMemSize")]
    [Hle("sceM4vdecQueryDecoderMemSizeInternal")]
    [Hle("_sceM4vdecQueryDecoderMemSizeInternal")]
    int M4vQueryDecoderMemSize(uint codecType, uint query, uint info)
    {
        if (codecType != TYPE_HW_M4VDEC) return ERR_INVALID_PARAM;
        if (query == 0 || info == 0) return ERR_INVALID_POINTER;
        if (!ValidQuery(query, out uint w, out uint h, out _)) return ERR_INVALID_PARAM;
        GuestMemory.W32(info, (w * h * 3 / 2 * 2 + 0x3FFFF) & ~0x3FFFFu);
        return 0;
    }

    [Hle("_sceM4vdecCreateDecoder")]
    [Hle("sceM4vdecCreateDecoderInternal")]
    [Hle("_sceM4vdecCreateDecoderInternal")]
    int M4vCreateDecoder(uint codecType, uint ctrl, uint query)
    {
        if (codecType != TYPE_HW_M4VDEC) return ERR_INVALID_PARAM;
        if (ctrl == 0 || query == 0) return ERR_INVALID_POINTER;
        if (!ValidQuery(query, out uint w, out uint h, out _)) return ERR_INVALID_PARAM;
        var d = new Decoder { Codec = FFmpegVideoDecoder.Create(FFmpeg.AutoGen.AVCodecID.AV_CODEC_ID_MPEG4, FFmpeg.AutoGen.AVHWDeviceType.AV_HWDEVICE_TYPE_NONE), MaxWidth = w, MaxHeight = h };
        if (d.Codec == null) Util.Log.WarnOnce("m4vdec-noffmpeg", "sceM4vdec: FFmpeg is unavailable, MPEG-4 videos will be black");
        lock (_decoders)
        {
            uint handle = _nextHandle++;
            _decoders[handle] = d;
            GuestMemory.W32(ctrl, handle);
        }
        return 0;
    }

    [Hle("sceM4vdecDecode")]
    [Hle("_sceM4vdecDecode")]
    int M4vDecode(uint ctrl, uint au, uint array) => sceAvcdecDecode(ctrl, au, array);

    [Hle("sceM4vdecDecodeWithWorkPicture")]
    [Hle("_sceM4vdecDecodeWithWorkPicture")]
    int M4vDecodeWithWork(uint ctrl, uint au, uint work, uint array) => sceAvcdecDecode(ctrl, au, array);

    [Hle("_sceM4vdecDecodeStop")]
    int M4vDecodeStop(uint ctrl, uint array) => sceAvcdecDecodeStop(ctrl, array);

    [Hle("sceM4vdecDecodeStopWithWorkPicture")]
    [Hle("_sceM4vdecDecodeStopWithWorkPicture")]
    int M4vDecodeStopWithWork(uint ctrl, uint work, uint array) => sceAvcdecDecodeStop(ctrl, array);

    [Hle("sceM4vdecDecodeFlush")]
    [Hle("_sceM4vdecDecodeFlush")]
    int M4vDecodeFlush(uint ctrl) => sceAvcdecDecodeFlush(ctrl);

    [Hle("sceM4vdecDecodeAvailableSize")]
    [Hle("_sceM4vdecDecodeAvailableSize")]
    int M4vDecodeAvailableSize(uint ctrl) => sceAvcdecDecodeAvailableSize(ctrl);

    [Hle("sceM4vdecDeleteDecoder")]
    [Hle("_sceM4vdecDeleteDecoder")]
    int M4vDeleteDecoder(uint ctrl) => sceAvcdecDeleteDecoder(ctrl);
}
