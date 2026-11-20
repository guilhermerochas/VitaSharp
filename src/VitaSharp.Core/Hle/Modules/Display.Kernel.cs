using VitaSharp.Core.Kernel;
using VitaSharp.Core.Memory;
using VitaSharp.Core.SaveState;

namespace VitaSharp.Core.Hle.Modules;

/// <summary>
/// SceDisplayForDriver, the display driver's kernel entries (FW 3.74 display.skprx, decompiled, study only), on the
/// state the user calls already keep (the game's framebuffer, the vblank clock and callbacks). The driver keeps one
/// record per head (0 the handheld's panel, 1 the HDMI output, 2 the virtual head) with two planes each (0 the game,
/// 1 the system overlay): the enabled state, the output mode and refresh rate, the brightness, colour space and
/// inverted colours, the merge (overlay alpha), scale and viewport of each plane, the process that owns each plane and
/// the framebuffer callback. A retail PCH-1000 has an OLED panel on head 0 (device type 1, 0x103) and nothing on HDMI;
/// head 0 starts enabled at 59.94 Hz in output mode 0. Head and plane outside 0..1 (0..2 where the driver allows the
/// virtual head) are 0x80290000. The waits and vblank callbacks of a head are the user calls' (one vblank clock).
/// </summary>
public sealed partial class Display
{
    const int ErrorInvalidHead = unchecked((int)0x80290000);
    const int ErrorInvalidValue = unchecked((int)0x80290001);
    const int ErrorInvalidAddress = unchecked((int)0x80290002);
    const int ErrorInvalidPixelFormat = unchecked((int)0x80290003);
    const int ErrorNoFrameBuffer = unchecked((int)0x80290008);
    const int ErrorNotCallback = unchecked((int)0x800280A1);

    /// <summary>The enabled heads, a bit per head (head 0 from boot).</summary>
    [SnapshotMissing(1u)] // states of builds before it: the boot state
    uint _enabledHeads = 1;

    /// <summary>Per head: the output mode words (+0x28, +0x34 of the driver's record).</summary>
    [SnapshotMissing]
    readonly uint[] _headOutputMode = new uint[3];

    [SnapshotMissing]
    readonly uint[] _headOutputModeExtra = new uint[3];

    /// <summary>Per head: brightness, colour space mode, inverted colours, the overlay's merge value and alpha.</summary>
    [SnapshotMissing]
    readonly uint[] _headBrightness = new uint[3];

    [SnapshotMissing]
    readonly uint[] _headColorSpace = new uint[3];

    [SnapshotMissing]
    readonly bool[] _headInvertColors = new bool[3];

    [SnapshotMissing]
    readonly uint[] _headMerge = new uint[2];

    [SnapshotMissing]
    readonly uint[] _headMergeAlpha = new uint[2];

    /// <summary>Per head: the framebuffer callback (a callback UID; 0 none).</summary>
    [SnapshotMissing]
    readonly int[] _headFrameBufCallback = new int[3];

    /// <summary>Per head and plane (head * 2 + plane): the owner pid (-1 none), the scale flags and factor bits, the
    /// viewport (x, y, width, height).</summary>
    [SnapshotMissing]
    readonly int[] _planeOwnerPlusOne = new int[4];

    [SnapshotMissing]
    readonly uint[] _planeScaleFlags = new uint[4];

    [SnapshotMissing]
    readonly float[] _planeScale = new float[4];

    [SnapshotMissing]
    readonly uint[] _planeViewport = new uint[16];

    /// <summary>The words NID_086DEFB6 (framebuffer owner and pid) and NID_BC76296A set.</summary>
    [SnapshotMissing]
    uint _frameBufOwnerWord, _frameBufOwnerPid, _driverWordB224;

    static bool HeadAndPlane(uint head, uint plane) => head <= 1 && plane <= 1;

    /// <summary>NID_086DEFB6(value, pid): sets the driver's framebuffer owner (the caller's process for pid 0); only for
    /// a caller the access control manager allows, which kernel code is.</summary>
    [Hle(Nid = 0x086DEFB6, Library = "SceDisplayForDriver")]
    void NID_086DEFB6(uint value, uint pid)
    {
        _frameBufOwnerWord = value;
        _frameBufOwnerPid = pid != 0 ? pid : (uint)ProcessScope.CurrentPid;
    }

    /// <summary>NID_332C5410(head): whether the head's display link is up (an enabled head 0 is; HDMI has nothing
    /// connected); head 3 and above 0x80290000.</summary>
    [Hle(Nid = 0x332C5410, Library = "SceDisplayForDriver")]
    int NID_332C5410(uint head)
    {
        if (head >= 3)
        {
            return ErrorInvalidHead;
        }

        return head == 0 && (_enabledHeads & 1) != 0 ? 1 : 0;
    }

    /// <summary>NID_3D95D478(head, value): an LCD panel's setting; head 0 only (0x80290000), and the PCH-1000's panel is
    /// an OLED: 0.</summary>
    [Hle(Nid = 0x3D95D478, Library = "SceDisplayForDriver")]
    int NID_3D95D478(uint head, uint value) => head != 0 ? ErrorInvalidHead : 0;

    /// <summary>NID_BC76296A(value): sets a word of the driver (the application manager's); 0.</summary>
    [Hle(Nid = 0xBC76296A, Library = "SceDisplayForDriver")]
    int NID_BC76296A(uint value)
    {
        _driverWordB224 = value;
        return 0;
    }

    /// <summary>ksceDisplayEnableHead (0x496032D6)(head): head 0 or 1.</summary>
    [Hle(Nid = 0x496032D6, Library = "SceDisplayForDriver")]
    int ksceDisplayEnableHead(uint head) => SetHeadEnabled(head, true);

    /// <summary>ksceDisplayDisableHead (0x43347565)(head): head 0 or 1.</summary>
    [Hle(Nid = 0x43347565, Library = "SceDisplayForDriver")]
    int ksceDisplayDisableHead(uint head) => SetHeadEnabled(head, false);

    int SetHeadEnabled(uint head, bool enabled)
    {
        if (head > 1)
        {
            return ErrorInvalidHead;
        }

        _enabledHeads = enabled ? _enabledHeads | (1u << (int)head) : _enabledHeads & ~(1u << (int)head);
        return 0;
    }

    /// <summary>ksceDisplayGetDeviceType (0x8D9A1CCE)(head, uint *type): head 0 the OLED panel (1, *type 0x103), head 1
    /// HDMI with nothing connected (0, 0); head 2 0x80290000 (*type 0).</summary>
    [Hle(Nid = 0x8D9A1CCE, Library = "SceDisplayForDriver")]
    int ksceDisplayGetDeviceType(uint head, uint typeOut)
    {
        if (head >= 3)
        {
            return ErrorInvalidHead;
        }

        int result = head switch
        {
            0 => 1,
            1 => 0,
            _ => ErrorInvalidHead,
        };
        if (typeOut != 0)
        {
            GuestMemory.W32(typeOut, head == 0 ? 0x103u : 0u);
        }

        return result;
    }

    /// <summary>ksceDisplayGetOutputMode (0xD2CED235)(head, uint *mode, uint *extra): the head's output mode (head 0
    /// from boot 0, 0).</summary>
    [Hle(Nid = 0xD2CED235, Library = "SceDisplayForDriver")]
    int ksceDisplayGetOutputMode(uint head, uint modeOut, uint extraOut)
    {
        if (head >= 3)
        {
            return ErrorInvalidHead;
        }

        WriteIfGiven(modeOut, _headOutputMode[head]);
        WriteIfGiven(extraOut, _headOutputModeExtra[head]);
        return 0;
    }

    /// <summary>ksceDisplaySetOutputMode (0xAF5EE5BE)(head, extra, mode): head 0 takes modes 0, 0x100000, 0x60800000,
    /// 0x60900000; head 1 the HDMI modes; another mode 0x80290001; head 2 and above 0x80290000. The refresh rate stays
    /// 59.94 Hz.</summary>
    [Hle(Nid = 0xAF5EE5BE, Library = "SceDisplayForDriver")]
    int ksceDisplaySetOutputMode(uint head, uint extra, uint mode)
    {
        if (head > 1)
        {
            return ErrorInvalidHead;
        }

        bool known = head == 0
            ? mode is 0 or 0x100000 or 0x60800000 or 0x60900000
            : mode is 0 or 0x8000 or 0x108000 or 0x100000;
        if (!known)
        {
            return ErrorInvalidValue;
        }

        _headOutputMode[head] = mode;
        _headOutputModeExtra[head] = extra;
        return 0;
    }

    /// <summary>ksceDisplayGetRefreshRateInternal (0x7911958E)(head, float *rate, uint *extra): 59.94 Hz on an enabled
    /// head, 0 on the others.</summary>
    [Hle(Nid = 0x7911958E, Library = "SceDisplayForDriver")]
    int ksceDisplayGetRefreshRateInternal(uint head, uint rateOut, uint extraOut)
    {
        if (head >= 3)
        {
            return ErrorInvalidHead;
        }

        WriteIfGiven(rateOut, head == 0 ? BitConverter.SingleToUInt32Bits(59.94f) : 0u);
        WriteIfGiven(extraOut, 0);
        return 0;
    }

    static void WriteIfGiven(uint address, uint value)
    {
        if (address != 0)
        {
            GuestMemory.W32(address, value);
        }
    }

    /// <summary>ksceDisplaySetBrightness (0x9E3C6DC6)(head, level): the panel's (head 0) or the output's level; head 2
    /// and above 0x80290000.</summary>
    [Hle(Nid = 0x9E3C6DC6, Library = "SceDisplayForDriver")]
    int ksceDisplaySetBrightness(uint head, uint level)
    {
        if (head > 1)
        {
            return ErrorInvalidHead;
        }

        _headBrightness[head] = level;
        return 0;
    }

    /// <summary>ksceDisplaySetColorSpaceMode (0x8D79D187)(head, mode): head 0 only.</summary>
    [Hle(Nid = 0x8D79D187, Library = "SceDisplayForDriver")]
    int ksceDisplaySetColorSpaceMode(uint head, uint mode)
    {
        if (head != 0)
        {
            return ErrorInvalidHead;
        }

        _headColorSpace[0] = mode;
        return 0;
    }

    /// <summary>ksceDisplaySetInvertColors (0x19140ACD)(head, enable): head 0 or 1 (the panel's colour matrices
    /// inverted).</summary>
    [Hle(Nid = 0x19140ACD, Library = "SceDisplayForDriver")]
    int ksceDisplaySetInvertColors(uint head, uint enable)
    {
        if (head > 1)
        {
            return ErrorInvalidHead;
        }

        _headInvertColors[head] = enable != 0;
        return 0;
    }

    /// <summary>ksceDisplaySetMergeConf (0x6B198052)(head, merge, alpha): the overlay plane's merge (alpha up to 0x100,
    /// else 0x80290001).</summary>
    [Hle(Nid = 0x6B198052, Library = "SceDisplayForDriver")]
    int ksceDisplaySetMergeConf(uint head, uint merge, uint alpha)
    {
        if (head > 1)
        {
            return ErrorInvalidHead;
        }

        if (alpha > 0x100)
        {
            return ErrorInvalidValue;
        }

        _headMerge[head] = merge;
        _headMergeAlpha[head] = alpha;
        return 0;
    }

    /// <summary>ksceDisplaySetScaleConf (0xEB390A76)(float scale, head, plane, flags): with flag bit 0 a scale in
    /// [0.8, 1.2] and no bits 0xC0, without it any number (NaN 0x80290001).</summary>
    [Hle(Nid = 0xEB390A76, Library = "SceDisplayForDriver")]
    void ksceDisplaySetScaleConf(HleContext context)
    {
        float scale = context.Fpr(0);
        uint head = context.Gpr(0);
        uint plane = context.Gpr(1);
        uint flags = context.Gpr(2);
        if (!HeadAndPlane(head, plane))
        {
            context.Return(ErrorInvalidHead);
            return;
        }

        bool badScale = (flags & 1) != 0 ? (flags & 0xC0) != 0 || scale < 0.8f || scale > 1.2f : float.IsNaN(scale);
        if (badScale)
        {
            context.Return(ErrorInvalidValue);
            return;
        }

        _planeScaleFlags[head * 2 + plane] = flags;
        _planeScale[head * 2 + plane] = scale;
        context.Return(0);
    }

    /// <summary>ksceDisplaySetViewportConf (0xEE5EB52D)(head, plane, conf): the plane's viewport (conf 0x14 bytes: x,
    /// y, width, height; NULL the whole screen).</summary>
    [Hle(Nid = 0xEE5EB52D, Library = "SceDisplayForDriver")]
    int ksceDisplaySetViewportConf(uint head, uint plane, uint conf)
    {
        if (!HeadAndPlane(head, plane))
        {
            return ErrorInvalidHead;
        }

        if (conf != 0 && GuestMemory.R32(conf) != 0x14)
        {
            return ErrorInvalidValue;
        }

        uint slot = (head * 2 + plane) * 4;
        for (uint i = 0; i < 4; i++)
        {
            _planeViewport[slot + i] = conf != 0 ? GuestMemory.R32(conf + 4 + i * 4) : 0;
        }

        return 0;
    }

    /// <summary>ksceDisplayGetActualViewportConf (0x40ACFE51)(head, plane, conf): the viewport in use (conf 0x14
    /// bytes, else 0x80290001): zeros while no process owns the plane, else the one set (the whole 960x544 screen for
    /// none).</summary>
    [Hle(Nid = 0x40ACFE51, Library = "SceDisplayForDriver")]
    int ksceDisplayGetActualViewportConf(uint head, uint plane, uint conf)
    {
        if (!HeadAndPlane(head, plane))
        {
            return ErrorInvalidHead;
        }

        if (conf == 0 || GuestMemory.R32(conf) != 0x14)
        {
            return ErrorInvalidValue;
        }

        uint slot = (head * 2 + plane) * 4;
        bool owned = _planeOwnerPlusOne[head * 2 + plane] != 0;
        bool whole = _planeViewport[slot + 2] == 0 || _planeViewport[slot + 3] == 0;
        GuestMemory.W32(conf + 0x04, owned && !whole ? _planeViewport[slot] : 0);
        GuestMemory.W32(conf + 0x08, owned && !whole ? _planeViewport[slot + 1] : 0);
        GuestMemory.W32(conf + 0x0C, owned ? (whole ? 960u : _planeViewport[slot + 2]) : 0);
        GuestMemory.W32(conf + 0x10, owned ? (whole ? 544u : _planeViewport[slot + 3]) : 0);
        return 0;
    }

    /// <summary>ksceDisplaySetOwner (0xB54962A1)(head, plane, pid): the process whose framebuffer the plane shows (-1
    /// none).</summary>
    [Hle(Nid = 0xB54962A1, Library = "SceDisplayForDriver")]
    int ksceDisplaySetOwner(uint head, uint plane, int pid)
    {
        if (!HeadAndPlane(head, plane))
        {
            return ErrorInvalidHead;
        }

        _planeOwnerPlusOne[head * 2 + plane] = pid + 1;
        return 0;
    }

    /// <summary>ksceDisplayGetProcFrameBufInternal (0x3BC165EF)(pid, head, plane, info): the framebuffer process pid
    /// (-1: the plane's owner, 0: the caller) set on the plane (info 0x2C bytes: +4 the pid, +8 its memory block, +0xC
    /// its physical address, +0x10 the SceDisplayFrameBuf words); NULL 0x80290002, another size 0x80290001.</summary>
    [Hle(Nid = 0x3BC165EF, Library = "SceDisplayForDriver")]
    int ksceDisplayGetProcFrameBufInternal(int pid, uint head, uint plane, uint info)
    {
        if (!HeadAndPlane(head, plane))
        {
            return ErrorInvalidHead;
        }

        if (info == 0)
        {
            return ErrorInvalidAddress;
        }

        if (GuestMemory.R32(info) != 0x2C)
        {
            return ErrorInvalidValue;
        }

        if (pid == -1)
        {
            pid = _planeOwnerPlusOne[head * 2 + plane] - 1;
        }
        else if (pid == 0)
        {
            pid = ProcessScope.CurrentPid;
        }

        bool shown = head == 0 && plane == 0;
        GuestMemory.Span(info + 4, 0x28).Clear();
        GuestMemory.W32(info + 0x04, (uint)pid);
        GuestMemory.W32(info + 0x0C, shown ? _base : 0);
        GuestMemory.W32(info + 0x10, 0x18);
        GuestMemory.W32(info + 0x14, shown ? _base : 0);
        GuestMemory.W32(info + 0x18, shown ? _pitch : 0);
        GuestMemory.W32(info + 0x1C, shown ? _format : 0);
        GuestMemory.W32(info + 0x20, shown ? _width : 0);
        GuestMemory.W32(info + 0x24, shown ? _height : 0);
        return 0;
    }

    /// <summary>ksceDisplayRegisterFrameBufCallbackInternal (0xFA7CE579)(head, callback): the callback the driver
    /// notifies when the head's framebuffer changes; not a callback 0x800280A1.</summary>
    [Hle(Nid = 0xFA7CE579, Library = "SceDisplayForDriver")]
    int ksceDisplayRegisterFrameBufCallbackInternal(uint head, int callback)
    {
        if (head > 2)
        {
            return ErrorInvalidHead;
        }

        if (K.Objects.Get<Callback>(callback) is null)
        {
            return ErrorNotCallback;
        }

        _headFrameBufCallback[head] = callback;
        return 0;
    }

    /// <summary>ksceDisplayRegisterFrameBufCallback (0x6E22990E)(callback): the same for the primary head.</summary>
    [Hle(Nid = 0x6E22990E, Library = "SceDisplayForDriver")]
    int ksceDisplayRegisterFrameBufCallback(int callback) => ksceDisplayRegisterFrameBufCallbackInternal(0, callback);

    /// <summary>ksceDisplayRegisterVblankStartCallbackInternal (0x4AE2A2B1)(head, callback): the head's vblank start
    /// callback (the one vblank clock).</summary>
    [Hle(Nid = 0x4AE2A2B1, Library = "SceDisplayForDriver")]
    int ksceDisplayRegisterVblankStartCallbackInternal(uint head, int callback)
    {
        return head >= 3 ? ErrorInvalidHead : sceDisplayRegisterVblankStartCallback(callback);
    }

    /// <summary>ksceDisplayUnregisterVblankStartCallbackInternal (0xB027433E)(head, callback).</summary>
    [Hle(Nid = 0xB027433E, Library = "SceDisplayForDriver")]
    int ksceDisplayUnregisterVblankStartCallbackInternal(uint head, int callback)
    {
        return head >= 3 ? ErrorInvalidHead : sceDisplayUnregisterVblankStartCallback(callback);
    }

    /// <summary>ksceDisplayWaitVblankStartInternal (0xB80CA224)(head).</summary>
    [Hle(Nid = 0xB80CA224, Library = "SceDisplayForDriver", Cost = HleCost.SchedulerWait)]
    int ksceDisplayWaitVblankStartInternal(uint head) => head >= 3 ? ErrorInvalidHead : sceDisplayWaitVblankStart();

    /// <summary>ksceDisplayWaitVblankStartCBInternal (0x8F3C0E19)(head).</summary>
    [Hle(Nid = 0x8F3C0E19, Library = "SceDisplayForDriver", Cost = HleCost.SchedulerWait, CallbackWait = true)]
    int ksceDisplayWaitVblankStartCBInternal(uint head) => head >= 3 ? ErrorInvalidHead : sceDisplayWaitVblankStartCB();

    /// <summary>ksceDisplayWaitVblankStartMultiInternal (0x32287576)(head, count).</summary>
    [Hle(Nid = 0x32287576, Library = "SceDisplayForDriver", Cost = HleCost.SchedulerWait)]
    int ksceDisplayWaitVblankStartMultiInternal(uint head, uint count) => head >= 3 ? ErrorInvalidHead : sceDisplayWaitVblankStartMulti(count);

    /// <summary>ksceDisplayWaitVblankStartMultiCBInternal (0x61421AAF)(head, count).</summary>
    [Hle(Nid = 0x61421AAF, Library = "SceDisplayForDriver", Cost = HleCost.SchedulerWait, CallbackWait = true)]
    int ksceDisplayWaitVblankStartMultiCBInternal(uint head, uint count) => head >= 3 ? ErrorInvalidHead : sceDisplayWaitVblankStartMultiCB(count);

    /// <summary>ksceDisplayWaitSetFrameBufInternal (0x12A77662)(head, plane).</summary>
    [Hle(Nid = 0x12A77662, Library = "SceDisplayForDriver", Cost = HleCost.SchedulerWait)]
    int ksceDisplayWaitSetFrameBufInternal(uint head, uint plane) => head >= 3 ? ErrorInvalidHead : sceDisplayWaitSetFrameBuf();

    /// <summary>ksceDisplayWaitSetFrameBufCBInternal (0x35466D63)(head, plane).</summary>
    [Hle(Nid = 0x35466D63, Library = "SceDisplayForDriver", Cost = HleCost.SchedulerWait, CallbackWait = true)]
    int ksceDisplayWaitSetFrameBufCBInternal(uint head, uint plane) => head >= 3 ? ErrorInvalidHead : sceDisplayWaitSetFrameBufCB();

    /// <summary>ksceDisplayWaitSetFrameBufMultiInternal (0xF83C95B1)(head, plane, count).</summary>
    [Hle(Nid = 0xF83C95B1, Library = "SceDisplayForDriver", Cost = HleCost.SchedulerWait)]
    int ksceDisplayWaitSetFrameBufMultiInternal(uint head, uint plane, uint count) => head >= 3 ? ErrorInvalidHead : sceDisplayWaitSetFrameBufMulti(count);

    /// <summary>ksceDisplayWaitSetFrameBufMultiCBInternal (0x6DC8F0F5)(head, plane, count).</summary>
    [Hle(Nid = 0x6DC8F0F5, Library = "SceDisplayForDriver", Cost = HleCost.SchedulerWait, CallbackWait = true)]
    int ksceDisplayWaitSetFrameBufMultiCBInternal(uint head, uint plane, uint count) => head >= 3 ? ErrorInvalidHead : sceDisplayWaitSetFrameBufMultiCB(count);

    // ------------------------------------------------------------------ framebuffer capture

    /// <summary>The capture of the plane's framebuffer into the caller's buffer (FW 3.74 ksceDisplayCaptureFrameBuf*):
    /// the destination (0x18 bytes: +4 its base, its low byte clear, +8 its pitch, +0xC its 32-bit pixel format, +0x10
    /// width, +0x14 height; another size 0x80290001, an unknown format 0x80290003) gets the shown framebuffer scaled to
    /// its size (nearest pixel; the DMA and IFTU paths differ only in the engine); nothing set 0x80290008.</summary>
    int CaptureFrameBuf(uint head, uint plane, uint destination)
    {
        if (!HeadAndPlane(head, plane))
        {
            return ErrorInvalidHead;
        }

        if (destination == 0)
        {
            return ErrorInvalidAddress;
        }

        if (GuestMemory.R32(destination) != 0x18)
        {
            return ErrorInvalidValue;
        }

        uint target = GuestMemory.R32(destination + 4);
        uint targetPitch = GuestMemory.R32(destination + 8);
        uint targetFormat = GuestMemory.R32(destination + 0xC);
        uint targetWidth = GuestMemory.R32(destination + 0x10);
        uint targetHeight = GuestMemory.R32(destination + 0x14);
        if ((target & 0xFF) != 0)
        {
            return ErrorInvalidAddress;
        }

        if (targetFormat is not (0 or 0x8000 or 0x100000 or 0x108000 or 0x60800000 or 0x60808000 or 0x60900000 or 0x60908000))
        {
            return ErrorInvalidPixelFormat;
        }

        if (targetPitch * targetHeight == 0)
        {
            return ErrorInvalidValue;
        }

        if (head != 0 || plane != 0 || _base == 0 || _width == 0 || _height == 0)
        {
            return ErrorNoFrameBuffer;
        }

        for (uint y = 0; y < targetHeight; y++)
        {
            uint sourceY = y * _height / targetHeight;
            for (uint x = 0; x < targetWidth; x++)
            {
                uint sourceX = x * _width / targetWidth;
                GuestMemory.W32(target + (y * targetPitch + x) * 4, GuestMemory.R32(_base + (sourceY * _pitch + sourceX) * 4));
            }
        }

        return 0;
    }

    /// <summary>ksceDisplayCaptureFrameBufDMACInternal (0x707EEE2E)(pid, head, plane, dst).</summary>
    [Hle(Nid = 0x707EEE2E, Library = "SceDisplayForDriver")]
    int ksceDisplayCaptureFrameBufDMACInternal(int pid, uint head, uint plane, uint destination) => CaptureFrameBuf(head, plane, destination);

    /// <summary>ksceDisplayCaptureFrameBufIFTUInternal (0xD4C812E5)(pid, head, plane, dst).</summary>
    [Hle(Nid = 0xD4C812E5, Library = "SceDisplayForDriver")]
    int ksceDisplayCaptureFrameBufIFTUInternal(int pid, uint head, uint plane, uint destination) => CaptureFrameBuf(head, plane, destination);

    /// <summary>ksceDisplayCaptureFrameBufDMAC (0xF116D0B4)(pid, dst): the primary head's game plane.</summary>
    [Hle(Nid = 0xF116D0B4, Library = "SceDisplayForDriver")]
    int ksceDisplayCaptureFrameBufDMAC(int pid, uint destination) => CaptureFrameBuf(0, 0, destination);

    /// <summary>ksceDisplayCaptureFrameBufIFTU (0xB0CED8BC)(pid, dst): the primary head's game plane.</summary>
    [Hle(Nid = 0xB0CED8BC, Library = "SceDisplayForDriver")]
    int ksceDisplayCaptureFrameBufIFTU(int pid, uint destination) => CaptureFrameBuf(0, 0, destination);
}
