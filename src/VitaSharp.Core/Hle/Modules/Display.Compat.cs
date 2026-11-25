using VitaSharp.Core.Gpu;
using VitaSharp.Core.Memory;
using VitaSharp.Core.SaveState;
using VitaSharp.Core.Util;

namespace VitaSharp.Core.Hle.Modules;

/// <summary>
/// sceDisplaySetFrameBufForCompat (SceDisplayUser) and its syscall _sceDisplaySetFrameBufForCompat (SceDisplay
/// 0x45BCB941), FW 3.74 display.skprx (decompiled, study only): ScePspemu's plane over the PSP picture, a framebuffer
/// placed at (x, y) with a size on the screen. The user call takes (x, y, width, height, pParam, opt) and the syscall
/// (x, y, width, args) with args = {height, pParam, opt, the sizes of pParam and opt}. pParam null removes the plane;
/// else it is a SceDisplayFrameBuf of size 0x1C (else 0x80290001) with a base 256-byte aligned and not 0
/// (0x80290002), a pitch a multiple of 64 pixels from the width to the width + 2047 (4095 for the other format;
/// 0x80290004), the pixel format
/// A8B8G8R8 (0) or the 0x50000000 format (else 0x80290003), and only system programs may call it (0x80010058). The
/// syscall refuses a pParam size above 0x1C or an opt size above 0x18 (0x8002710B).
/// <para>
/// It is plane 0, not a second plane: FUN_810023b8 programs the head's plane 0 IFTU unit with the framebuffer scaled
/// into the destination rectangle (x, y, width - 1, height - 1 in the descriptor) and writes the process's plane 0
/// record, the one sceDisplaySetFrameBuf writes (ksceDisplaySetFrameBufInternal clears the record's rectangle): the
/// last of the two calls is what the screen shows, sceDisplayGetFrameBuf reads it back, and null leaves the plane
/// without a framebuffer. Bit 20 of the SceDisplayFrameBuf's flags is the IFTU's filter (bilinear; 0x100000 is what the
/// driver uses without a pParam). With opt the IFTU writes the picture into the opt buffer instead of the screen and
/// the plane is left as it was. The merge settings (ksceDisplaySetMergeConf) are plane 1's, the shell's overlay, drawn
/// over this plane.
/// </para>
/// </summary>
public sealed partial class Display
{
    const int ErrorInvalidPitch = unchecked((int)0x80290004);
    const int ErrorNotSystemProgram = unchecked((int)0x80010058);
    const int ErrorArgumentTooLarge = unchecked((int)0x8002710B);

    /// <summary>The compat plane's format other than A8B8G8R8 the driver accepts.</summary>
    public const uint CompatPlaneFormatOther = 0x50000000;

    [SnapshotMissing(0)]
    int _compatX;

    [SnapshotMissing(0)]
    int _compatY;

    [SnapshotMissing(0)]
    int _compatWidth;

    [SnapshotMissing(0)]
    int _compatHeight;

    [SnapshotMissing(0u)]
    uint _compatBase;

    [SnapshotMissing(0u)]
    uint _compatPitch;

    [SnapshotMissing(0u)]
    uint _compatFormat;

    [SnapshotMissing(0u)]
    uint _compatFlags;

    /// <summary>Plane 0's framebuffer is the compat one, placed in its rectangle (a sceDisplaySetFrameBuf since shows
    /// its framebuffer whole).</summary>
    [SnapshotMissing(false)]
    bool _compatPlaced;

    /// <summary>The compat framebuffer: its place and size on the screen and its framebuffer (base 0: plane 0 shows no
    /// compat framebuffer).</summary>
    public (int X, int Y, int Width, int Height, uint Base, uint Pitch, uint PixelFormat) CompatPlane => _compatPlaced
        ? (_compatX, _compatY, _compatWidth, _compatHeight, _compatBase, _compatPitch, _compatFormat)
        : (_compatX, _compatY, _compatWidth, _compatHeight, 0u, 0u, 0u);

    /// <summary>Where plane 0's framebuffer goes on the screen: its compat rectangle, or null (the whole screen).</summary>
    internal DisplayPlacement? CurrentPlacement => _compatPlaced && _base != 0
        ? new DisplayPlacement(_compatX, _compatY, _compatWidth, _compatHeight, (_compatFlags & 0x1FFFFF) >> 20 != 0)
        : null;

    /// <summary>(x, y, width, height, pParam, opt): the user call.</summary>
    [Hle("sceDisplaySetFrameBufForCompat")]
    int SetFrameBufForCompat(int x, int y, int width, int height, uint pParam, uint opt)
    {
        return SetCompatPlane(x, y, width, height, pParam, opt);
    }

    /// <summary>(x, y, width, args): the syscall, args = {height, pParam, opt, pParam's size, opt's size}.</summary>
    [Hle("_sceDisplaySetFrameBufForCompat")]
    int SetFrameBufForCompatSyscall(int x, int y, int width, uint args)
    {
        if (args == 0)
        {
            return ErrorInvalidAddress;
        }

        int height = (int)GuestMemory.R32(args);
        uint pParam = GuestMemory.R32(args + 4);
        uint opt = GuestMemory.R32(args + 8);
        uint paramSize = GuestMemory.R32(args + 12);
        uint optSize = GuestMemory.R32(args + 16);
        if ((pParam != 0 && paramSize > 0x1C) || (opt != 0 && optSize > 0x18))
        {
            return ErrorArgumentTooLarge;
        }

        return SetCompatPlane(x, y, width, height, pParam, opt);
    }

    int SetCompatPlane(int x, int y, int width, int height, uint pParam, uint opt)
    {
        if (!Emu.IsSystemProgram)
        {
            return ErrorNotSystemProgram;
        }

        if (pParam == 0)
        {
            // plane 0's record has no framebuffer (the IFTU unit gets an empty input)
            _compatPlaced = false;
            _compatBase = 0;
            _compatPitch = 0;
            _compatFormat = 0;
            _base = 0;
            return 0;
        }

        if (GuestMemory.R32(pParam) != 0x1C)
        {
            return ErrorInvalidValue;
        }

        uint frameBase = GuestMemory.R32(pParam + 4);
        uint pitch = GuestMemory.R32(pParam + 8);
        uint format = GuestMemory.R32(pParam + 12);
        uint frameWidth = GuestMemory.R32(pParam + 16);
        uint frameHeight = GuestMemory.R32(pParam + 20);
        uint flags = GuestMemory.R32(pParam + 24);
        if (frameBase == 0 || (frameBase & 0xFF) != 0)
        {
            return ErrorInvalidAddress;
        }

        if (format != 0 && format != CompatPlaneFormatOther)
        {
            return ErrorInvalidPixelFormat;
        }

        // the pitch is a multiple of 64 and at most 2048 (A8B8G8R8) or 4096 pixels wider than the frame (unsigned)
        int slackShift = format == 0 ? 11 : 12;
        if ((pitch & 0x3F) != 0 || ((pitch - frameWidth) >> slackShift) != 0)
        {
            return ErrorInvalidPitch;
        }

        if (pitch * ((frameHeight + 7) & ~7u) == 0)
        {
            return ErrorInvalidValue;
        }

        if (opt != 0)
        {
            // the IFTU converts the picture into the opt buffer (SceIftuForDriver_67E37EFC) and the screen is untouched
            Log.WarnOnce("compatopt", "[display] sceDisplaySetFrameBufForCompat with opt: the conversion into memory is not done");
            return 0;
        }

        _compatX = x;
        _compatY = y;
        _compatWidth = width;
        _compatHeight = height;
        _compatBase = frameBase;
        _compatPitch = pitch;
        _compatFormat = format;
        _compatFlags = flags;
        _compatPlaced = true;
        // the record sceDisplayGetFrameBuf reads, and what the screen shows from now on
        _base = frameBase;
        _pitch = pitch;
        _format = format;
        _width = frameWidth;
        _height = frameHeight;
        _lastSetVcount = VBlank.Count;
        PresentCurrent();
        return 0;
    }
}
