using VitaSharp.Core.Memory;
using VitaSharp.Core.SaveState;

namespace VitaSharp.Core.Hle.Modules;

// SceIftuForDriver (os0:kd/lowio.skprx 3.74, read in the module, study only): the display's IFTU planes, the
// hardware that scales, converts and merges the frame buffers SceDisplay hands it. The module keeps, per plane (0..4,
// 0..3 for enable, disable and the output format; else 0x803F0700), the two 15-word conversion blocks (a NULL block
// re-applies the kept one), the input frame buffer (0x54 bytes, its pixel format from a fixed set, else 0x803F0703),
// the output format (pitch a multiple of 16 and height of 8, else 0x803F0701; a format of a fixed set, else
// 0x803F0703; only while the plane is off, else 0x803F0704), and on planes 1 and 3 the alpha (0..0x100) and merge
// settings; enabling programs the registers from them (state 2 until the first frame, then 1) and disabling clears
// it. The emulated display composes the frames itself (SceDisplay HLE), so the planes keep these values and program
// nothing.
public sealed partial class SystemServicesVsh
{
    const int IftuPlanes = 5, IftuEnablePlanes = 4, IftuBlockWords = 15, IftuFrameBufferWords = 0x54 / 4;

    /// <summary>SceIftu: a plane out of range (0x803F0700).</summary>
    const int IFTU_ERR_PLANE = unchecked((int)0x803F0700);

    /// <summary>SceIftu: a value out of range or misaligned (0x803F0701).</summary>
    const int IFTU_ERR_VALUE = unchecked((int)0x803F0701);

    /// <summary>SceIftu: a pixel format outside the module's set (0x803F0703).</summary>
    const int IFTU_ERR_FORMAT = unchecked((int)0x803F0703);

    /// <summary>SceIftu: the plane is enabled (0x803F0704).</summary>
    const int IFTU_ERR_ENABLED = unchecked((int)0x803F0704);

    /// <summary>The plane states of the module: off, on, on and waiting for its first frame.</summary>
    const int IftuOff = 0, IftuOnStarting = 2;

    [SnapshotMissing] // states of builds before it: every plane off, nothing set
    readonly int[] _iftuState = new int[IftuPlanes];

    [SnapshotMissing]
    readonly uint[] _iftuConversion = new uint[IftuPlanes * IftuBlockWords * 2];

    [SnapshotMissing]
    readonly uint[] _iftuFrameBuffer = new uint[IftuPlanes * IftuFrameBufferWords];

    [SnapshotMissing]
    readonly uint[] _iftuOutput = new uint[IftuPlanes * 3];

    [SnapshotMissing]
    readonly uint[] _iftuBlend = new uint[IftuPlanes * 2];

    /// <summary>A plane's state, its input pixel format, its output format and its alpha, for the tests.</summary>
    internal (int State, uint InputFormat, uint OutputFormat, uint Alpha) IftuPlane(int plane) =>
        (_iftuState[plane], _iftuFrameBuffer[plane * IftuFrameBufferWords], _iftuOutput[plane * 3 + 2], _iftuBlend[plane * 2]);

    /// <summary>The pixel formats an input frame buffer may have.</summary>
    static bool IftuInputFormat(uint format)
    {
        return format is 0 or 1 or 2 or 4 or 8 or 0x10 or 0x20 or 0x40 or 0x80 or 0x100 or 0x1000 or 0x2000 or 0x4000
            or 0x8000 or 0x10000 or 0x20000 or 0x200000 or 0x80020000 or 0x80140000 or 0x80180000;
    }

    /// <summary>NID_0FCBF457 (SceIftuForDriver)(int plane, const void *block): the plane's first conversion block.</summary>
    [Hle("NID_0FCBF457", Nid = 0x0FCBF457, Library = "SceIftuForDriver")]
    int IftuSetConversion(uint plane, uint block)
    {
        return IftuKeepBlock(plane, block, 0);
    }

    /// <summary>NID_D64F4C6B (SceIftuForDriver)(int plane, const void *block): the plane's second conversion block.</summary>
    [Hle("NID_D64F4C6B", Nid = 0xD64F4C6B, Library = "SceIftuForDriver")]
    int IftuSetConversion2(uint plane, uint block)
    {
        return IftuKeepBlock(plane, block, 1);
    }

    int IftuKeepBlock(uint plane, uint block, int which)
    {
        if (plane >= IftuPlanes)
        {
            return IFTU_ERR_PLANE;
        }

        if (block != 0)
        {
            int at = ((int)plane * 2 + which) * IftuBlockWords;
            for (int word = 0; word < IftuBlockWords; word++)
            {
                _iftuConversion[at + word] = GuestMemory.R32(block + (uint)word * 4);
            }
        }

        return 0;
    }

    /// <summary>ksceIftuSetInputFrameBuffer(int plane, const SceIftuFrameBuf *fb, int src, int): the plane's input
    /// (NULL re-applies the kept one); its pixel format must be one the hardware reads.</summary>
    [Hle(Nid = 0x7CE0C4DA, Library = "SceIftuForDriver")]
    int ksceIftuSetInputFrameBuffer(uint plane, uint frameBuffer, int source, int flags)
    {
        if (plane >= IftuPlanes)
        {
            return IFTU_ERR_PLANE;
        }

        if (frameBuffer != 0)
        {
            if (!IftuInputFormat(GuestMemory.R32(frameBuffer)))
            {
                return IFTU_ERR_FORMAT;
            }

            for (int word = 0; word < IftuFrameBufferWords; word++)
            {
                _iftuFrameBuffer[(int)plane * IftuFrameBufferWords + word] = GuestMemory.R32(frameBuffer + (uint)word * 4);
            }
        }

        return 0;
    }

    /// <summary>ksceIftuSetOutputFormat(int plane, uint pitch, uint height, uint format): only while the plane is off.</summary>
    [Hle(Nid = 0xE6EE2C6B, Library = "SceIftuForDriver")]
    int ksceIftuSetOutputFormat(uint plane, uint pitch, uint height, uint format)
    {
        if (plane >= IftuEnablePlanes)
        {
            return IFTU_ERR_PLANE;
        }

        if ((pitch & 0xF) != 0 || (height & 7) != 0)
        {
            return IFTU_ERR_VALUE;
        }

        if (format is not (0x10 or 0x20 or 0x40 or 0x80 or 0x1000 or 0x2000 or 0x4000 or 0x8000))
        {
            return IFTU_ERR_FORMAT;
        }

        if (_iftuState[plane] != IftuOff)
        {
            return IFTU_ERR_ENABLED;
        }

        _iftuOutput[plane * 3] = pitch & 0xFFFF;
        _iftuOutput[plane * 3 + 1] = height;
        _iftuOutput[plane * 3 + 2] = format & 0xFFFF;
        return 0;
    }

    /// <summary>NID_357EAE24 (SceIftuForDriver)(int plane, uint alpha): planes 1 and 3; alpha 0..0x100 (0x100 opaque).</summary>
    [Hle("NID_357EAE24", Nid = 0x357EAE24, Library = "SceIftuForDriver")]
    int IftuSetAlpha(uint plane, uint alpha)
    {
        if ((plane & ~2u) != 1)
        {
            return IFTU_ERR_PLANE;
        }

        if (alpha > 0x100)
        {
            return IFTU_ERR_VALUE;
        }

        _iftuBlend[plane * 2] = alpha;
        return 0;
    }

    /// <summary>ksceIftuSetMergeSetting(int plane, int setting): planes 1 and 3 (0x80: no merge).</summary>
    [Hle(Nid = 0xAF19FD85, Library = "SceIftuForDriver")]
    int ksceIftuSetMergeSetting(uint plane, uint setting)
    {
        if ((plane & ~2u) != 1)
        {
            return IFTU_ERR_PLANE;
        }

        _iftuBlend[plane * 2 + 1] = setting;
        return 0;
    }

    /// <summary>ksceIftuEnable(int plane): turns the plane on from its kept settings (nothing when on already); 0.</summary>
    [Hle(Nid = 0x0D7C02F7, Library = "SceIftuForDriver")]
    int ksceIftuEnable(uint plane)
    {
        if (plane >= IftuEnablePlanes)
        {
            return IFTU_ERR_PLANE;
        }

        if (_iftuState[plane] == IftuOff)
        {
            _iftuState[plane] = IftuOnStarting;
        }

        return 0;
    }

    /// <summary>ksceIftuDisable(int plane): turns the plane off; 0.</summary>
    [Hle(Nid = 0xC11F30B3, Library = "SceIftuForDriver")]
    int ksceIftuDisable(uint plane)
    {
        if (plane >= IftuEnablePlanes)
        {
            return IFTU_ERR_PLANE;
        }

        _iftuState[plane] = IftuOff;
        return 0;
    }
}
