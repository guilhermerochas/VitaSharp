using VitaSharp.Core.Memory;
using VitaSharp.Core.SaveState;

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
/// The plane is kept (<see cref="CompatPlane"/>); the compositing of a second plane over the game's is not done by the
/// presenter yet (ScePspemu's own picture of the PSP screen goes through the main plane, sceCompatSetDisplayConfig).
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

    /// <summary>The compat plane: its place and size on the screen and its framebuffer (base 0: no plane).</summary>
    public (int X, int Y, int Width, int Height, uint Base, uint Pitch, uint PixelFormat) CompatPlane =>
        (_compatX, _compatY, _compatWidth, _compatHeight, _compatBase, _compatPitch, _compatFormat);

    /// <summary>(x, y, width, height, pParam, opt): the user call.</summary>
    [Hle("sceDisplaySetFrameBufForCompat")]
    int SetFrameBufForCompat(int x, int y, int width, int height, uint pParam, uint opt)
    {
        return SetCompatPlane(x, y, width, height, pParam);
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

        return SetCompatPlane(x, y, width, height, pParam);
    }

    int SetCompatPlane(int x, int y, int width, int height, uint pParam)
    {
        if (!Emu.IsSystemProgram)
        {
            return ErrorNotSystemProgram;
        }

        if (pParam == 0)
        {
            _compatBase = 0;
            _compatPitch = 0;
            _compatFormat = 0;
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

        _compatX = x;
        _compatY = y;
        _compatWidth = width;
        _compatHeight = height;
        _compatBase = frameBase;
        _compatPitch = pitch;
        _compatFormat = format;
        return 0;
    }
}
